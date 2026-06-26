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

using DS4Windows.DS4Control;
using DS4WinWPF.DS4Control;
using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using NLog;
using Sensorit.Base;
using SharpOSC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DS4WinWPF.DS4Forms;
using static DS4Windows.Global;

namespace DS4Windows
{
    public class ControlService
    {
        public ViGEmClient vigemTestClient = null;
        private readonly DualSenseAudioPassthrough dualSenseAudioPassthrough = new DualSenseAudioPassthrough();
        private readonly DualSenseMicrophonePassthrough dualSenseMicrophonePassthrough = new DualSenseMicrophonePassthrough();
        private readonly GameBarIntegration gameBarIntegration = new GameBarIntegration();
        private readonly object hidHideSessionLock = new object();
        private readonly HashSet<string> hidHideSessionManagedInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> hidHidePersistentManagedInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool? hidHideActiveStateBeforeManagedSession;
        // Might be useful for ScpVBus build
        public const int EXPANDED_CONTROLLER_COUNT = 8;
        public const int MAX_DS4_CONTROLLER_COUNT = Global.MAX_DS4_CONTROLLER_COUNT;
#if FORCE_4_INPUT
        public static int CURRENT_DS4_CONTROLLER_LIMIT = Global.OLD_XINPUT_CONTROLLER_COUNT;
#else
        public static int CURRENT_DS4_CONTROLLER_LIMIT = Global.IsWin8OrGreater() ? MAX_DS4_CONTROLLER_COUNT : Global.OLD_XINPUT_CONTROLLER_COUNT;
#endif
        public static bool USING_MAX_CONTROLLERS = CURRENT_DS4_CONTROLLER_LIMIT == EXPANDED_CONTROLLER_COUNT;
        public DS4Device[] DS4Controllers = new DS4Device[MAX_DS4_CONTROLLER_COUNT];
        public int activeControllers = 0;
        public Mouse[] touchPad = new Mouse[MAX_DS4_CONTROLLER_COUNT];
        public bool running = false;
        public bool loopControllers = true;
        public bool inServiceTask = false;
        private DS4State[] MappedState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private DS4State[] CurrentState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private DS4State[] PreviousState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private DS4State[] TempState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        public DS4StateExposed[] ExposedState = new DS4StateExposed[MAX_DS4_CONTROLLER_COUNT];
        public ControllerSlotManager slotManager = new ControllerSlotManager();
        public bool recordingMacro = false;
        public event EventHandler<DebugEventArgs> Debug = null;
        bool[] buttonsdown = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        bool[] held = new bool[MAX_DS4_CONTROLLER_COUNT];
        int[] oldmouse = new int[MAX_DS4_CONTROLLER_COUNT] { -1, -1, -1, -1, -1, -1, -1, -1 };
        private int[] startupReportDiagCounts = new int[MAX_DS4_CONTROLLER_COUNT];
        private int[] viiperInputLayerDiagCounts = new int[MAX_DS4_CONTROLLER_COUNT];
        private System.Threading.Timer gameBarProfileTimer;
        private int gameBarProfileUpdateGate = 0;
        public OutputDevice[] outputDevices = new OutputDevice[MAX_DS4_CONTROLLER_COUNT] { null, null, null, null, null, null, null, null };
        private OneEuroFilter3D[] udpEuroPairAccel = new OneEuroFilter3D[UdpServer.NUMBER_SLOTS]
        {
            new OneEuroFilter3D(), new OneEuroFilter3D(),
            new OneEuroFilter3D(), new OneEuroFilter3D(),
        };
        private OneEuroFilter3D[] udpEuroPairGyro = new OneEuroFilter3D[UdpServer.NUMBER_SLOTS]
        {
            new OneEuroFilter3D(), new OneEuroFilter3D(),
            new OneEuroFilter3D(), new OneEuroFilter3D(),
        };
        Thread tempThread;
        Thread tempBusThread;
        Thread eventDispatchThread;
        Dispatcher eventDispatcher;
        public bool suspending;

        private UdpServer _udpServer;
        private OutputSlotManager outputslotMan;

        private HashSet<string> hidDeviceHidingAffectedDevs = new HashSet<string>();
        private HashSet<string> hidDeviceHidingExemptedDevs = new HashSet<string>();
        private bool hidDeviceHidingForced = false;
        private bool hidDeviceHidingEnabled = false;
        private bool stickMouseFakerInputNoticeShown = false;
        private bool stickMouseFakerInputMissingNoticeShown = false;
        private readonly object outputKbmHandlerLock = new object();

        private ControlServiceDeviceOptions deviceOptions;
        public ControlServiceDeviceOptions DeviceOptions { get => deviceOptions; }

        private DS4WinWPF.ArgumentParser cmdParser;
        private static readonly Logger startupDiagLogger = LogManager.GetCurrentClassLogger();

        public event EventHandler ServiceStarted;
        public event EventHandler PreServiceStop;
        public event EventHandler ServiceStopped;
        public event EventHandler RunningChanged;
        //public event EventHandler HotplugFinished;
        public delegate void HotplugControllerHandler(ControlService sender, DS4Device device, int index);
        public event HotplugControllerHandler HotplugController;

        private byte[][] udpOutBuffers = new byte[UdpServer.NUMBER_SLOTS][]
        {
            new byte[UdpServer.DATA_RSP_PACKET_LEN], new byte[UdpServer.DATA_RSP_PACKET_LEN],
            new byte[UdpServer.DATA_RSP_PACKET_LEN], new byte[UdpServer.DATA_RSP_PACKET_LEN],
        };

        private DS4State[] oscState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        public HandleOscPacket oscCallback;

        public UDPListener oscListener;
        public UDPSender oscSender;

        void GetPadDetailForIdx(int padIdx, ref DualShockPadMeta meta)
        {
            //meta = new DualShockPadMeta();
            meta.PadId = (byte)padIdx;
            meta.Model = DsModel.DS4;

            var d = DS4Controllers[padIdx];
            if (d == null)
            {
                meta.PadMacAddress = null;
                meta.PadState = DsState.Disconnected;
                meta.ConnectionType = DsConnection.None;
                meta.Model = DsModel.None;
                meta.BatteryStatus = 0;
                meta.IsActive = false;
                return;
                //return meta;
            }

            bool isValidSerial = false;
            string stringMac = d.getMacAddress();
            if (!string.IsNullOrEmpty(stringMac))
            {
                stringMac = string.Join("", stringMac.Split(':'));
                //stringMac = stringMac.Replace(":", "").Trim();
                meta.PadMacAddress = System.Net.NetworkInformation.PhysicalAddress.Parse(stringMac);
                isValidSerial = d.isValidSerial();
            }

            if (!isValidSerial)
            {
                //meta.PadMacAddress = null;
                meta.PadState = DsState.Disconnected;
            }
            else
            {
                if (d.isSynced() || d.IsAlive())
                    meta.PadState = DsState.Connected;
                else
                    meta.PadState = DsState.Reserved;
            }

            meta.ConnectionType = (d.getConnectionType() == ConnectionType.USB) ? DsConnection.Usb : DsConnection.Bluetooth;
            meta.IsActive = !d.isDS4Idle();

            int batteryLevel = d.getBattery();
            if (d.isCharging() && batteryLevel >= 100)
                meta.BatteryStatus = DsBattery.Charged;
            else
            {
                if (batteryLevel >= 95)
                    meta.BatteryStatus = DsBattery.Full;
                else if (batteryLevel >= 70)
                    meta.BatteryStatus = DsBattery.High;
                else if (batteryLevel >= 50)
                    meta.BatteryStatus = DsBattery.Medium;
                else if (batteryLevel >= 20)
                    meta.BatteryStatus = DsBattery.Low;
                else if (batteryLevel >= 5)
                    meta.BatteryStatus = DsBattery.Dying;
                else
                    meta.BatteryStatus = DsBattery.None;
            }

            //return meta;
        }

        public ControlService(DS4WinWPF.ArgumentParser cmdParser)
        {
            this.cmdParser = cmdParser;

            Crc32Algorithm.InitializeTable(DS4Device.DefaultPolynomial);

            eventDispatchThread = new Thread(() =>
            {
                Dispatcher currentDis = Dispatcher.CurrentDispatcher;
                eventDispatcher = currentDis;
                Dispatcher.Run();
            });
            eventDispatchThread.IsBackground = true;
            eventDispatchThread.Priority = ThreadPriority.BelowNormal;
            eventDispatchThread.Name = "ControlService Events";
            eventDispatchThread.Start();

            for (int i = 0, arlength = DS4Controllers.Length; i < arlength; i++)
            {
                MappedState[i] = new DS4State();
                CurrentState[i] = new DS4State();
                TempState[i] = new DS4State();
                PreviousState[i] = new DS4State();
                ExposedState[i] = new DS4StateExposed(CurrentState[i]);
                oscState[i] = new DS4State();

                int tempDev = i;
                Global.L2OutputSettings[i].TwoStageModeChanged += (sender, e) =>
                {
                    Mapping.l2TwoStageMappingData[tempDev].Reset();
                };

                Global.R2OutputSettings[i].TwoStageModeChanged += (sender, e) =>
                {
                    Mapping.r2TwoStageMappingData[tempDev].Reset();
                };
            }

            outputslotMan = new OutputSlotManager();
            //outputslotMan.SlotAssigned += OutputslotMan_SlotAssigned;
            deviceOptions = Global.DeviceOptions;

            DS4Devices.RequestElevation += DS4Devices_RequestElevation;
            DS4Devices.PrepareDS4Init = PrepareDS4DeviceInit;
            DS4Devices.PostDS4Init = PostDS4DeviceInit;
            DS4Devices.PreparePendingDevice = CheckForSupportedDevice;
            outputslotMan.ViGEmFailure += OutputslotMan_ViGEmFailure;

            Global.UDPServerSmoothingMincutoffChanged += ChangeUdpSmoothingAttrs;
            Global.UDPServerSmoothingBetaChanged += ChangeUdpSmoothingAttrs;

            CreateOSCCallback();

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            //oscListener = new UDPListener(Global.getOSCServerPortNum(), callback: oscCallback);
            //AppLogger.LogToGui("OSC LISTENER STARTED", false);
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            Global.PrepareAbsMonitorBounds(string.Empty);
        }

        //private void OutputslotMan_SlotAssigned(OutputSlotManager sender, int slotNum, OutSlotDevice outSlotDev)
        //{
        //    LogDebug($"Associated input controller #{outSlotDev.InputIndex + 1} ({outSlotDev.InputDisplayString}) to virtual {outSlotDev.OutputDevice.GetDeviceType()} Controller in{(outSlotDev.PermanentType != OutContType.None ? " permanent" : "")} output slot #{outSlotDev.Index + 1}");
        //}

        private string[] MapMonitoringOscMessageToCommand(string[] command)
        {
            // Overwrite "monitor" with the controller Id
            command[2] = command[3];

            switch (command[4])
            {
                case "battery":
                    command[3] = "battery";
                    break;
                case "l2":
                case "r2":
                    command[3] = "trigger";
                    break;
                case "rx":
                case "ry":
                case "lx":
                case "ly":
                    command[3] = "stick";
                    break;
                default:
                    command[3] = "press";
                    break;
            }

            return command;
        }

        private void CreateOSCCallback()
        {
            oscCallback = delegate (OscPacket packet)
            {
                var messageReceived = (OscMessage)packet;

                // If typecase fails, exit
                if (messageReceived == null)
                {
                    return;
                }

                string[] command = null;
                try
                {
                    command = messageReceived.Address.Split("/");
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui("Error Receiving OSC Message: " + e.Message, false, true);
                }

                if (command == null)
                {
                    return;
                }

                if (command[1] != "ds4windows")
                {
                    return;
                }

                if (command[2] == "monitor")
                {
                    if (Global.isInterpretingOscMonitoring())
                    {
                        command = MapMonitoringOscMessageToCommand(command);
                    }
                    else
                    {
                        return;
                    }
                }

                int stateInd = -1;
                if (!int.TryParse(command[2], out stateInd))
                {
                    stateInd = -1;
                }

                if (stateInd == -1)
                {
                    AppLogger.LogToGui("Received malformed OSC address: " + messageReceived.Address, false);
                    return;
                }

                if (command[3] == "battery")
                {
                    if (!isUsingOSCSender())
                    {
                        AppLogger.LogToGui("Battery level requested, but the OSC Sender isn't active. Turn it on in Settings.", false);
                    }
                    else
                    {
                        oscSender.Send(new SharpOSC.OscMessage("/ds4windows/monitor/" + stateInd + "/battery", oscState[stateInd].Battery));
                    }
                    return;
                }
                else if (command[3] == "press")
                {
                    int messageValue = Convert.ToInt32(messageReceived.Arguments[0]);
                    bool buttonBool = messageValue == 1 ? true : false;

                    switch (command[4])
                    {
                        case "cross":
                            oscState[stateInd].Cross = buttonBool;
                            break;
                        case "square":
                            oscState[stateInd].Square = buttonBool;
                            break;
                        case "circle":
                            oscState[stateInd].Circle = buttonBool;
                            break;
                        case "triangle":
                            oscState[stateInd].Triangle = buttonBool;
                            break;
                        case "r1":
                            oscState[stateInd].R1 = buttonBool;
                            break;
                        case "r2":
                            oscState[stateInd].R2 = Convert.ToByte(buttonBool ? 255 : 0);
                            break;
                        case "r3":
                            oscState[stateInd].R3 = buttonBool;
                            break;
                        case "l1":
                            oscState[stateInd].L1 = buttonBool;
                            break;
                        case "l2":
                            oscState[stateInd].L2 = Convert.ToByte(buttonBool ? 255 : 0);
                            break;
                        case "l3":
                            oscState[stateInd].L3 = buttonBool;
                            break;
                        case "dpadup":
                        case "dup":
                            oscState[stateInd].DpadUp = buttonBool;
                            break;
                        case "dpaddown":
                        case "ddown":
                            oscState[stateInd].DpadDown = buttonBool;
                            break;
                        case "dpadleft":
                        case "dleft":
                            oscState[stateInd].DpadLeft = buttonBool;
                            break;
                        case "dpadright":
                        case "dright":
                            oscState[stateInd].DpadRight = buttonBool;
                            break;
                        case "options":
                            oscState[stateInd].Options = buttonBool;
                            break;
                        case "share":
                            oscState[stateInd].Share = buttonBool;
                            break;
                    }
                }
                else if (command[3] == "stick" && messageReceived.Arguments.Count == 1)
                {
                    switch (command[4])
                    {
                        case "lx":
                            oscState[stateInd].LX = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                        case "ly":
                            oscState[stateInd].LY = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                        case "rx":
                            oscState[stateInd].RX = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                        case "ry":
                            oscState[stateInd].RY = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                    }
                }
                else if (command[3] == "stick" && messageReceived.Arguments.Count == 2)
                {
                    float xValue = Convert.ToSingle(messageReceived.Arguments[0]);
                    float yValue = Convert.ToSingle(messageReceived.Arguments[1]);

                    if (command[4] == "left")
                    {
                        oscState[stateInd].LX = Convert.ToByte(xValue * 255);
                        oscState[stateInd].LY = Convert.ToByte(yValue * 255);
                    }
                    else if (command[4] == "right")
                    {
                        oscState[stateInd].RX = Convert.ToByte(xValue * 255);
                        oscState[stateInd].RY = Convert.ToByte(yValue * 255);
                    }
                }
                else if (command[3] == "trigger")
                {
                    switch (command[4])
                    {
                        case "r2":
                            oscState[stateInd].R2 = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                        case "l2":
                            oscState[stateInd].L2 = Convert.ToByte(Convert.ToSingle(messageReceived.Arguments[0]));
                            break;
                    }
                }
            };
        }

        public void RefreshOutputKBMHandler()
        {
            lock (outputKbmHandlerLock)
            {
                if (Global.outputKBMHandler != null)
                {
                    Global.outputKBMHandler.Disconnect();
                    Global.outputKBMHandler = null;
                }

                if (Global.outputKBMMapping != null)
                {
                    Global.outputKBMMapping = null;
                }

                InitOutputKBMHandler();
            }
        }

        private void InitOutputKBMHandler()
        {
            string attemptVirtualkbmHandler = cmdParser.VirtualkbmHandler;
            InitOutputKBMHandler(attemptVirtualkbmHandler);
        }

        private void InitOutputKBMHandler(string attemptVirtualkbmHandler)
        {
            StartupDiag($"InitOutputKBMHandler begin requested={attemptVirtualkbmHandler}");
            Global.InitOutputKBMHandler(attemptVirtualkbmHandler);
            StartupDiag($"InitOutputKBMHandler created handler={Global.outputKBMHandler?.GetIdentifier()}");

            bool handlerConnected = false;
            try
            {
                StartupDiag($"OutputKBM.Connect begin handler={Global.outputKBMHandler?.GetIdentifier()}");
                handlerConnected = Global.outputKBMHandler.Connect();
                StartupDiag($"OutputKBM.Connect end handler={Global.outputKBMHandler?.GetIdentifier()} connected={handlerConnected}");
            }
            catch (Exception ex)
            {
                StartupDiag($"OutputKBM.Connect exception handler={Global.outputKBMHandler?.GetIdentifier()} {ex.GetType().Name}: {ex.Message}");
            }

            if (!handlerConnected &&
                attemptVirtualkbmHandler != VirtualKBMFactory.GetFallbackHandlerIdentifier())
            {
                StartupDiag($"OutputKBM falling back to {VirtualKBMFactory.GetFallbackHandlerIdentifier()}");
                Global.outputKBMHandler = VirtualKBMFactory.GetFallbackHandler();
            }
            else
            {
                // Connection was made. Check if version number should get populated
                if (outputKBMHandler.GetIdentifier() == FakerInputHandler.IDENTIFIER)
                {
                    Global.outputKBMHandler.Version = Global.fakerInputVersion;
                }
            }

            Global.InitOutputKBMMapping(Global.outputKBMHandler.GetIdentifier());
            Global.outputKBMMapping.PopulateConstants();
            Global.outputKBMMapping.PopulateMappings();
            StartupDiag($"InitOutputKBMHandler end active={Global.outputKBMHandler?.GetFullDisplayName()} mapping={Global.outputKBMMapping?.GetType().Name}");
        }

        private bool SwitchOutputKBMHandler(string identifier)
        {
            lock (outputKbmHandlerLock)
            {
                if (Global.outputKBMHandler != null &&
                    Global.outputKBMHandler.GetIdentifier() == identifier)
                {
                    return true;
                }

                VirtualKBMBase oldHandler = Global.outputKBMHandler;
                VirtualKBMMapping oldMapping = Global.outputKBMMapping;

                try
                {
                    InitOutputKBMHandler(identifier);
                    if (Global.outputKBMHandler?.GetIdentifier() == identifier)
                    {
                        RefreshLoadedActionAliases();
                        oldHandler?.Disconnect();
                        return true;
                    }
                }
                catch { }

                Global.outputKBMHandler?.Disconnect();
                Global.outputKBMHandler = oldHandler;
                Global.outputKBMMapping = oldMapping;
                return false;
            }
        }

        private void EnsureVirtualMouseForStickMouseProfile(int ind)
        {
            if (!ProfileUsesStickMouse(ind))
            {
                return;
            }

            if (Global.outputKBMHandler?.GetIdentifier() == FakerInputHandler.IDENTIFIER)
            {
                return;
            }

            Global.RefreshFakerInputInfo();
            if (Global.fakerInputInstalled)
            {
                bool switched = SwitchOutputKBMHandler(FakerInputHandler.IDENTIFIER);
                if (switched && !stickMouseFakerInputNoticeShown)
                {
                    stickMouseFakerInputNoticeShown = true;
                    LogDebug("Stick mouse profile detected. Using FakerInput virtual mouse so Windows keeps a real pointer device available.");
                }
                else if (!switched && !stickMouseFakerInputMissingNoticeShown)
                {
                    stickMouseFakerInputMissingNoticeShown = true;
                    LogDebug("Stick mouse profile detected, but DS4Windows could not connect to FakerInput. SendInput will remain active.");
                }

                return;
            }

            if (!stickMouseFakerInputMissingNoticeShown)
            {
                stickMouseFakerInputMissingNoticeShown = true;
                string helpURL = "https://github.com/Ryochan7/FakerInput/";
                LogDebug($"Stick mouse profile detected, but FakerInput is not installed. Install FakerInput to expose a persistent virtual mouse and avoid hidden cursor behavior on couch/TV setups: {helpURL}");
                AppLogger.LogToTray("Stick mouse works best with FakerInput installed for a persistent virtual mouse.");
            }
        }

        private static bool ProfileUsesStickMouse(int ind)
        {
            return StickDirectionMapsToMouse(ind, DS4Controls.LXNeg) ||
                StickDirectionMapsToMouse(ind, DS4Controls.LXPos) ||
                StickDirectionMapsToMouse(ind, DS4Controls.LYNeg) ||
                StickDirectionMapsToMouse(ind, DS4Controls.LYPos) ||
                StickDirectionMapsToMouse(ind, DS4Controls.RXNeg) ||
                StickDirectionMapsToMouse(ind, DS4Controls.RXPos) ||
                StickDirectionMapsToMouse(ind, DS4Controls.RYNeg) ||
                StickDirectionMapsToMouse(ind, DS4Controls.RYPos);
        }

        private static bool StickDirectionMapsToMouse(int ind, DS4Controls control)
        {
            DS4ControlSettings setting = GetDS4CSetting(ind, control);
            return ActionMapsToMouse(setting.actionType, setting.action.actionBtn) ||
                ActionMapsToMouse(setting.shiftActionType, setting.shiftAction.actionBtn);
        }

        private static bool ActionMapsToMouse(DS4ControlSettings.ActionType actionType, X360Controls outputControl)
        {
            if (actionType != DS4ControlSettings.ActionType.Button)
            {
                return false;
            }

            return outputControl >= X360Controls.MouseUp &&
                outputControl <= X360Controls.AbsMouseRight;
        }

        private static void RefreshLoadedActionAliases()
        {
            for (int device = 0; device < Global.MAX_DS4_CONTROLLER_COUNT; device++)
            {
                foreach (DS4Controls control in Enum.GetValues(typeof(DS4Controls)))
                {
                    DS4ControlSettings setting = GetDS4CSetting(device, control);
                    Global.RefreshActionAlias(setting, false);
                    Global.RefreshActionAlias(setting, true);
                }
            }
        }

        private void OutputslotMan_ViGEmFailure(object sender, int errorCode)
        {
            eventDispatcher.BeginInvoke((Action)(() =>
            {
                loopControllers = false;
                while (inServiceTask)
                    Thread.SpinWait(1000);

                LogDebug(string.Format(DS4WinWPF.Translations.Strings.ViGEmPluginFailure, errorCode),
                    true);
                Stop();
            }));
        }

        public void PostDS4DeviceInit(DS4Device device)
        {
            if (device.DeviceType == InputDevices.InputDeviceType.JoyConL ||
                device.DeviceType == InputDevices.InputDeviceType.JoyConR)
            {
                if (deviceOptions.JoyConDeviceOpts.LinkedMode == JoyConDeviceOptions.LinkMode.Joined)
                {
                    InputDevices.JoyConDevice tempJoyDev = device as InputDevices.JoyConDevice;
                    tempJoyDev.PerformStateMerge = true;

                    if (device.DeviceType == InputDevices.InputDeviceType.JoyConL)
                    {
                        tempJoyDev.PrimaryDevice = true;
                        if (deviceOptions.JoyConDeviceOpts.JoinGyroProv == JoyConDeviceOptions.JoinedGyroProvider.JoyConL)
                        {
                            tempJoyDev.OutputMapGyro = true;
                        }
                        else
                        {
                            tempJoyDev.OutputMapGyro = false;
                        }
                    }
                    else
                    {
                        tempJoyDev.PrimaryDevice = false;
                        if (deviceOptions.JoyConDeviceOpts.JoinGyroProv == JoyConDeviceOptions.JoinedGyroProvider.JoyConR)
                        {
                            tempJoyDev.OutputMapGyro = true;
                        }
                        else
                        {
                            tempJoyDev.OutputMapGyro = false;
                        }
                    }
                }
            }
        }

        private void PrepareDS4DeviceSettingHooks(DS4Device device)
        {
            if (device.DeviceType == InputDevices.InputDeviceType.DualSense)
            {
                InputDevices.DualSenseDevice tempDSDev = device as InputDevices.DualSenseDevice;

                DualSenseControllerOptions dSOpts = tempDSDev.NativeOptionsStore;
                dSOpts.LedModeChanged += (sender, e) => { tempDSDev.CheckControllerNumDeviceSettings(activeControllers); };
            }
            else if (device.DeviceType == InputDevices.InputDeviceType.JoyConL ||
                device.DeviceType == InputDevices.InputDeviceType.JoyConR)
            {
            }
        }

        public bool CheckForSupportedDevice(HidDevice device, VidPidInfo metaInfo)
        {
            bool result = false;
            switch (metaInfo.inputDevType)
            {
                case InputDevices.InputDeviceType.DS4:
                    result = deviceOptions.DS4DeviceOpts.Enabled;
                    break;
                case InputDevices.InputDeviceType.DualSense:
                    result = deviceOptions.DualSenseOpts.Enabled;
                    break;
                case InputDevices.InputDeviceType.SwitchPro:
                    result = deviceOptions.SwitchProDeviceOpts.Enabled;
                    break;
                case InputDevices.InputDeviceType.JoyConL:
                case InputDevices.InputDeviceType.JoyConR:
                case InputDevices.InputDeviceType.JoyConGrip:
                    result = deviceOptions.JoyConDeviceOpts.Enabled;
                    break;
                case InputDevices.InputDeviceType.DS3:
                    result = deviceOptions.DS3DeviceOpts.Enabled;
                    break;
                default:
                    break;
            }

            return result;
        }

        public void PrepareDS4DeviceInit(DS4Device device)
        {
            // Does nothing now
        }

        public void ShutDown()
        {
            ReleaseHidHideManagedDevices();
            outputslotMan.ShutDown();
            OutputSlotPersist.WriteConfig(outputslotMan);

            eventDispatcher.InvokeShutdown();
            eventDispatcher = null;

            eventDispatchThread.Join();
            eventDispatchThread = null;
        }

        private void DS4Devices_RequestElevation(RequestElevationArgs args)
        {
            // Launches an elevated child process to re-enable device
            ProcessStartInfo startInfo =
                new ProcessStartInfo(Global.exelocation);
            startInfo.Verb = "runas";
            startInfo.Arguments = "re-enabledevice " + args.InstanceId;
            startInfo.UseShellExecute = true;

            try
            {
                Process child = Process.Start(startInfo);
                if (!child.WaitForExit(30000))
                {
                    child.Kill();
                }
                else
                {
                    args.StatusCode = child.ExitCode;
                }
                child.Dispose();
            }
            catch { }
        }

        public void CheckHidHidePresence(string ExePath = "", string ExeName = "Autoprofile Exe", bool AddExe = true) // Default value for D4W Startup
        {
            if (Global.hidHideInstalled)
            {
                LogDebug("HidHide control device found");
                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                {
                    if (!hidHideDevice.IsOpen())
                    {
                        return;
                    }
                    // Catch Blank Values and initialize for Startup. Also catches empty Values.
                    // Also Catches Empty values in auto-profiler, and defaults to trying to re-add D4W. Will fail harmlessly later.
                    if (ExePath == "") { ExePath = Global.exelocation; ExeName = "DS4Windows"; AddExe = true; }

                    // Check for inverse application cloak. If setting is being used in HidHide,
                    // skip checking HidHide whitelist for DS4Windows.
                    bool inverseAppCloak = hidHideDevice.GetWhiteListInverseState();
                    if (inverseAppCloak)
                    {
                        return;
                    }


                    List<string> dosPaths = hidHideDevice.GetWhitelist();

                    int maxPathCheckLength = 512;
                    StringBuilder sb = new StringBuilder(maxPathCheckLength);

                    DirectoryInfo dirInfo = new DirectoryInfo(Path.GetDirectoryName(ExePath));
                    // Check if exe is placed in a junction symlink directory (done with Scoop).
                    // Good enough
                    if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                        dirInfo.LinkTarget != null)
                    {
                        // App directory is a junction. Find real directory and get proper path
                        // for inserting into HidHide
                        ExePath = Path.Combine(dirInfo.LinkTarget, Path.GetFileName(ExePath));
                    }

                    string driveLetter = Path.GetPathRoot(ExePath).Replace("\\", "");
                    uint _ = NativeMethods.QueryDosDevice(driveLetter, sb, maxPathCheckLength);
                    //int error = Marshal.GetLastWin32Error();

                    string dosDrivePath = sb.ToString();
                    // Strip a possible \??\ prefix.
                    if (dosDrivePath.StartsWith(@"\??\"))
                    {
                        dosDrivePath = dosDrivePath.Remove(0, 4);
                    }

                    string partial = ExePath.Replace(driveLetter, "");
                    // Need to trim starting '\\' from path2 or Path.Combine will
                    // treat it as an absolute path and only return path2
                    string realPath = Path.Combine(dosDrivePath, partial.TrimStart('\\'));
                    bool exists = dosPaths.Contains(realPath);
                    if (!exists && AddExe)
                    {
                        LogDebug($"{ExeName} not found in HidHide whitelist. Adding to list");
                        dosPaths.Add(realPath);
                        hidHideDevice.SetWhitelist(dosPaths);
                    }
                    if (exists && !AddExe)
                    {
                        LogDebug($"{ExeName} found in HidHide whitelist. Removing from list");
                        dosPaths.Remove(realPath);
                        hidHideDevice.SetWhitelist(dosPaths);
                    }
                }
            }
        }

        public void LoadPermanentSlotsConfig()
        {
            OutputSlotPersist.ReadConfig(outputslotMan);
        }

        public void UpdateHidHideAttributes()
        {
            if (Global.hidHideInstalled)
            {
                hidDeviceHidingAffectedDevs.Clear();
                hidDeviceHidingExemptedDevs.Clear(); // No known equivalent in HidHide
                hidDeviceHidingForced = false; // No known equivalent in HidHide
                hidDeviceHidingEnabled = false;

                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice(writeAccess: false))
                {
                    if (!hidHideDevice.IsOpen())
                    {
                        return;
                    }

                    bool active = hidHideDevice.GetActiveState();
                    List<string> instances = hidHideDevice.GetBlacklist();

                    hidDeviceHidingEnabled = active;
                    foreach (string instance in instances)
                    {
                        hidDeviceHidingAffectedDevs.Add(instance.ToUpper());
                    }
                }
            }
        }

        public void UpdateHidHiddenAttributes()
        {
            if (Global.hidHideInstalled)
            {
                UpdateHidHideAttributes();
            }
        }

        private bool CheckAffected(DS4Device dev)
        {
            bool result = false;
            if (dev != null && hidDeviceHidingEnabled)
            {
                string deviceInstanceId = Global.GetInstanceIdFromDevicePath(dev.HidDevice.DevicePath);
                if (Global.hidHideInstalled)
                {
                    result = Global.CheckHidHideAffectedStatus(deviceInstanceId,
                        hidDeviceHidingAffectedDevs, hidDeviceHidingExemptedDevs, hidDeviceHidingForced);
                }
            }

            return result;
        }

        /// <summary>
        /// Obtain extra mappable controls not on a DS4 that should be added
        /// to the checked inputs list. Keeps Mapping class from having to check
        /// extra Switch Pro and JoyCon buttons for DS4 controllers
        /// </summary>
        /// <param name="dev">Instance of input device</param>
        /// <returns>List of extra controls to check in Mapping class</returns>
        private List<DS4Controls> GetKnownExtraButtons(DS4Device dev)
        {
            List<DS4Controls> result = new List<DS4Controls>();
            switch (dev.DeviceType)
            {
                case InputDevices.InputDeviceType.DualSense:
                    {
                        InputDevices.DualSenseDevice tempDev = dev as InputDevices.DualSenseDevice;
                        if (tempDev != null &&
                            tempDev.SubType == InputDevices.DualSenseDevice.DeviceSubType.DSEdge)
                        {
                            // Added extra DualSense Edge buttons as extra in the mapper.
                            // Keeps from checking non-existent buttons on other device types.
                            result.AddRange(new DS4Controls[] { DS4Controls.FnL, DS4Controls.FnR, DS4Controls.BLP, DS4Controls.BRP });
                        }
                    }

                    break;
                case InputDevices.InputDeviceType.JoyConL:
                case InputDevices.InputDeviceType.JoyConR:
                    result.AddRange(new DS4Controls[] { DS4Controls.Capture, DS4Controls.SideL, DS4Controls.SideR, DS4Controls.FnL, DS4Controls.FnR });
                    break;
                case InputDevices.InputDeviceType.SwitchPro:
                    result.AddRange(new DS4Controls[] { DS4Controls.Capture });
                    break;
                default:
                    break;
            }

            return result;
        }

        private void ChangeExclusiveStatus(DS4Device dev)
        {
            if (Global.hidHideInstalled)
            {
                dev.CurrentExclusiveStatus = DS4Device.ExclusiveStatus.HidHideAffected;
            }
        }

        /// <summary>
        /// Adds the device to HidHide for this DS4Windows run and enables hiding.
        /// Session entries stay active across Stop/Start inside this process and
        /// are automatically removed by HidHide when DS4Windows exits.
        /// </summary>
        private bool EnsureHidHideSessionForDevice(DS4Device dev)
        {
            if (!Global.hidHideInstalled || dev == null) return false;

            string instanceId = Global.GetInstanceIdFromDevicePath(dev.HidDevice.DevicePath);
            if (string.IsNullOrEmpty(instanceId)) return false;

            bool alreadyManaged;
            lock (hidHideSessionLock)
            {
                alreadyManaged = hidHideSessionManagedInstanceIds.Contains(instanceId) ||
                    hidHidePersistentManagedInstanceIds.Contains(instanceId);
            }

            try
            {
                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                {
                    if (!hidHideDevice.IsOpen()) return false;

                    bool active = hidHideDevice.GetActiveState();
                    lock (hidHideSessionLock)
                    {
                        hidHideActiveStateBeforeManagedSession ??= active;
                    }

                    if (!active)
                    {
                        hidHideDevice.SetActiveState(true);
                    }

                    if (!alreadyManaged && hidHideDevice.AddSessionBlacklist(new List<string> { instanceId }))
                    {
                        lock (hidHideSessionLock)
                        {
                            hidHideSessionManagedInstanceIds.Add(instanceId);
                        }

                        LogDebug($"HidHide session hiding enabled for {dev.DisplayName} ({instanceId})", false);
                    }
                    else if (!alreadyManaged && !EnsurePersistentHidHideBlacklist(hidHideDevice, instanceId, dev))
                    {
                        return false;
                    }

                    UpdateHidHideAttributes();
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"HidHide session setup failed for {dev.DisplayName}: {ex.Message}", true);
                return false;
            }
        }

        private bool EnsurePersistentHidHideBlacklist(HidHideAPIDevice hidHideDevice, string instanceId, DS4Device dev)
        {
            List<string> instances = hidHideDevice.GetBlacklist()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();

            if (instances.Any(item => string.Equals(item, instanceId, StringComparison.OrdinalIgnoreCase)))
            {
                StartupDiag($"HidHide persistent blacklist already contains {instanceId}");
                return true;
            }

            instances.Add(instanceId);
            if (!hidHideDevice.SetBlacklist(instances))
            {
                StartupDiag($"HidHide persistent blacklist fallback failed for {dev.DisplayName} ({instanceId})");
                return false;
            }

            lock (hidHideSessionLock)
            {
                hidHidePersistentManagedInstanceIds.Add(instanceId);
            }

            LogDebug($"HidHide persistent hiding enabled for {dev.DisplayName} ({instanceId})", false);
            return true;
        }

        private void ReleaseHidHideManagedDevices()
        {
            if (!Global.hidHideInstalled) return;

            List<string> persistentIds;
            bool? restoreActiveState;
            lock (hidHideSessionLock)
            {
                persistentIds = hidHidePersistentManagedInstanceIds.ToList();
                restoreActiveState = hidHideActiveStateBeforeManagedSession;
                hidHideSessionManagedInstanceIds.Clear();
                hidHidePersistentManagedInstanceIds.Clear();
                hidHideActiveStateBeforeManagedSession = null;
            }

            if (persistentIds.Count == 0 && restoreActiveState is null) return;

            try
            {
                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                {
                    if (!hidHideDevice.IsOpen()) return;

                    hidHideDevice.ClearSessionBlacklist();

                    if (persistentIds.Count > 0)
                    {
                        List<string> instances = hidHideDevice.GetBlacklist()
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .ToList();

                        int removed = instances.RemoveAll(item =>
                            persistentIds.Any(managed => string.Equals(managed, item, StringComparison.OrdinalIgnoreCase)));

                        if (removed > 0 && hidHideDevice.SetBlacklist(instances))
                        {
                            StartupDiag($"Released {removed} DS4Windows-managed HidHide blacklist entries");
                        }
                    }

                    if (restoreActiveState == false)
                    {
                        hidHideDevice.SetActiveState(false);
                    }

                    UpdateHidHideAttributes();
                }
            }
            catch (Exception ex)
            {
                StartupDiag($"ReleaseHidHideManagedDevices exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void EnsureHidHideForVirtualOutput(int index, DS4Device device, OutContType contType)
        {
            if (device == null || !DS4Devices.isExclusiveMode)
            {
                return;
            }

            if (contType != OutContType.X360 &&
                contType != OutContType.DS4 &&
                !ViiperOutDevice.IsViiperType(contType))
            {
                return;
            }

            if (EnsureHidHideSessionForDevice(device))
            {
                ChangeExclusiveStatus(device);
                StartupDiag($"HidHide virtual-output containment ready index={index} type={contType}");
            }
            else if (ViiperOutDevice.IsViiperType(contType))
            {
                LogDebug($"VIIPER {contType} output is active but the physical {device.DisplayName} could not be hidden with HidHide. Games may detect both the physical controller and the virtual controller.", true);
            }
        }

        private void TestQueueBus(Action temp)
        {
            eventDispatcher.BeginInvoke(() =>
            {
                temp?.Invoke();
            });
        }

        public void ChangeUDPStatus(bool state, bool openPort = true)
        {

            if (state && _udpServer == null)
            {
                udpChangeStatus = true;
                TestQueueBus(() =>
                {
                    _udpServer = new UdpServer(GetPadDetailForIdx);
                    if (openPort)
                    {
                        // Change thread affinity of object to have normal priority
                        Task.Run(() =>
                        {
                            var UDP_SERVER_PORT = Global.getUDPServerPortNum();
                            var UDP_SERVER_LISTEN_ADDRESS = Global.getUDPServerListenAddress();

                            try
                            {
                                _udpServer.Start(UDP_SERVER_PORT, UDP_SERVER_LISTEN_ADDRESS);
                                LogDebug($"UDP server listening on address {UDP_SERVER_LISTEN_ADDRESS} port {UDP_SERVER_PORT}");
                            }
                            catch (System.Net.Sockets.SocketException ex)
                            {
                                var errMsg = String.Format("Couldn't start UDP server on address {0}:{1}, outside applications won't be able to access pad data ({2})", UDP_SERVER_LISTEN_ADDRESS, UDP_SERVER_PORT, ex.SocketErrorCode);

                                LogDebug(errMsg, true);
                                AppLogger.LogToTray(errMsg, true, true);
                            }
                        }).Wait();
                    }

                    udpChangeStatus = false;
                });
            }
            else if (!state && _udpServer != null)
            {
                TestQueueBus(() =>
                {
                    udpChangeStatus = true;
                    _udpServer.Stop();
                    _udpServer = null;
                    AppLogger.LogToGui("Closed UDP server", false);
                    udpChangeStatus = false;

                    for (int i = 0; i < UdpServer.NUMBER_SLOTS; i++)
                    {
                        ResetUdpSmoothingFilters(i);
                    }
                });
            }
        }

        public void ChangeOSCListenerStatus(bool state)
        {
            if (state)
            {
                oscListener = new UDPListener(Global.getOSCServerPortNum(), callback: oscCallback);

                AppLogger.LogToGui("OSC LISTENER STARTED AT PORT: " + Global.getOSCServerPortNum(), false);
            }
            else
            {
                oscListener.Close();
                oscListener = null;
                AppLogger.LogToGui("OSC LISTENER STOPPED", false);
            }
        }

        public void ChangeOSCSenderStatus(bool state)
        {
            if (state)
            {
                AppLogger.LogToGui("OSC SENDER STARTED AT IP: " + Global.getOSCSenderAddress() + " PORT: " + Global.getOSCSenderPortNum(), false);
                oscSender = new UDPSender(Global.getOSCSenderAddress(), Global.getOSCSenderPortNum());
            }
            else
            {
                AppLogger.LogToGui("OSC SENDER STOPPED", false);
                if (oscSender == null) { return; }
                oscSender.Close();
                oscSender = null;
            }
        }

        public void ChangeMotionEventStatus(bool state)
        {
            IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            if (state)
            {
                int i = 0;
                foreach (DS4Device dev in devices)
                {
                    int tempIdx = i;
                    dev.queueEvent(() =>
                    {
                        if (i < UdpServer.NUMBER_SLOTS)
                        {
                            PrepareDevUDPMotion(dev, tempIdx);
                        }
                    });

                    i++;
                }
            }
            else
            {
                foreach (DS4Device dev in devices)
                {
                    dev.queueEvent(() =>
                    {
                        if (dev.MotionEvent != null)
                        {
                            dev.Report -= dev.MotionEvent;
                            dev.MotionEvent = null;
                        }
                    });
                }
            }
        }

        private bool udpChangeStatus = false;
        public bool changingUDPPort = false;
        public async void UseUDPPort()
        {
            changingUDPPort = true;
            IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
            foreach (DS4Device dev in devices)
            {
                dev.queueEvent(() =>
                {
                    if (dev.MotionEvent != null)
                    {
                        dev.Report -= dev.MotionEvent;
                    }
                });
            }

            await Task.Delay(100);

            var UDP_SERVER_PORT = Global.getUDPServerPortNum();
            var UDP_SERVER_LISTEN_ADDRESS = Global.getUDPServerListenAddress();

            try
            {
                _udpServer.Start(UDP_SERVER_PORT, UDP_SERVER_LISTEN_ADDRESS);
                foreach (DS4Device dev in devices)
                {
                    dev.queueEvent(() =>
                    {
                        if (dev.MotionEvent != null)
                        {
                            dev.Report += dev.MotionEvent;
                        }
                    });
                }
                LogDebug($"UDP server listening on address {UDP_SERVER_LISTEN_ADDRESS} port {UDP_SERVER_PORT}");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                var errMsg = String.Format("Couldn't start UDP server on address {0}:{1}, outside applications won't be able to access pad data ({2})", UDP_SERVER_LISTEN_ADDRESS, UDP_SERVER_PORT, ex.SocketErrorCode);

                LogDebug(errMsg, true);
                AppLogger.LogToTray(errMsg, true, true);
            }

            changingUDPPort = false;
        }

        private void WarnExclusiveModeFailure(DS4Device device)
        {
            if (DS4Devices.isExclusiveMode && !device.isExclusive())
            {
                string message = DS4WinWPF.Properties.Resources.CouldNotOpenDS4.Replace("*Mac address*", device.getMacAddress()) + " " +
                    DS4WinWPF.Properties.Resources.QuitOtherPrograms;
                LogDebug(message, true);
                AppLogger.LogToTray(message, true);
            }
        }

        private void StartViGEm()
        {
            StartupDiag("StartViGEm begin");
            // Refresh internal ViGEmBus info
            Global.RefreshViGEmBusInfo();
            StartupDiag($"StartViGEm info installed={Global.vigemInstalled} version={Global.vigembusVersion} supported={Global.IsRunningSupportedViGEmBus()}");
            if (Global.IsRunningSupportedViGEmBus())
            {
                tempThread = new Thread(() =>
                {
                    try
                    {
                        StartupDiag("ViGEmClient ctor begin");
                        vigemTestClient = new ViGEmClient();
                        StartupDiag("ViGEmClient ctor end success");
                    }
                    catch (Exception ex)
                    {
                        StartupDiag($"ViGEmClient ctor exception {ex.GetType().Name}: {ex.Message}");
                    }
                });
                tempThread.Priority = ThreadPriority.AboveNormal;
                tempThread.IsBackground = true;
                tempThread.Start();
                while (tempThread.IsAlive)
                {
                    Thread.SpinWait(500);
                }
            }

            tempThread = null;
            StartupDiag($"StartViGEm end clientCreated={vigemTestClient != null}");
        }

        private void StopViGEm()
        {
            if (vigemTestClient != null)
            {
                StartupDiag("StopViGEm Dispose begin");
                vigemTestClient.Dispose();
                StartupDiag("StopViGEm Dispose end");
                vigemTestClient = null;
            }
        }

        public void AssignInitialDevices()
        {
            foreach (OutSlotDevice slotDevice in outputslotMan.OutputSlots)
            {
                if (slotDevice.CurrentReserveStatus ==
                    OutSlotDevice.ReserveStatus.Permanent)
                {
                    OutputDevice outDevice = EstablishOutDevice(0, slotDevice.PermanentType);
                    outputslotMan.DeferredPlugin(outDevice, -1, "", outputDevices, slotDevice.PermanentType);
                }
            }
            /*OutSlotDevice slotDevice =
                outputslotMan.FindExistUnboundSlotType(OutContType.X360);

            if (slotDevice == null)
            {
                slotDevice = outputslotMan.FindOpenSlot();
                slotDevice.CurrentReserveStatus = OutSlotDevice.ReserveStatus.Permanent;
                slotDevice.PermanentType = OutContType.X360;
                OutputDevice outDevice = EstablishOutDevice(0, OutContType.X360);
                Xbox360OutDevice tempXbox = outDevice as Xbox360OutDevice;
                outputslotMan.DeferredPlugin(tempXbox, -1, outputDevices, OutContType.X360);
            }
            */

            /*slotDevice = outputslotMan.FindExistUnboundSlotType(OutContType.X360);
            if (slotDevice == null)
            {
                slotDevice = outputslotMan.FindOpenSlot();
                slotDevice.CurrentReserveStatus = OutSlotDevice.ReserveStatus.Permanent;
                slotDevice.DesiredType = OutContType.X360;
                OutputDevice outDevice = EstablishOutDevice(1, OutContType.X360);
                Xbox360OutDevice tempXbox = outDevice as Xbox360OutDevice;
                outputslotMan.DeferredPlugin(tempXbox, 1, outputDevices);
            }*/
        }

        private OutputDevice EstablishOutDevice(int index, OutContType contType)
        {
            StartupDiag($"EstablishOutDevice begin index={index} contType={contType} client={vigemTestClient != null}");
            OutputDevice temp = null;
            temp = outputslotMan.AllocateController(contType, vigemTestClient);
            StartupDiag($"EstablishOutDevice end index={index} contType={contType} result={temp?.GetType().Name ?? "null"}");
            return temp;
        }

        public void EstablishOutFeedback(int index, OutContType contType,
            OutputDevice outDevice, DS4Device device)
        {
            int devIndex = index;

            if (contType == OutContType.X360)
            {
                Xbox360OutDevice tempXbox = outDevice as Xbox360OutDevice;
                Nefarius.ViGEm.Client.Targets.Xbox360FeedbackReceivedEventHandler p = (sender, args) =>
                {
                    //Trace.WriteLine(string.Format("Rumble ({0}, {1}) {2}",
                    //    args.LargeMotor, args.SmallMotor, DateTime.Now.ToString("hh:mm:ss.FFFF")));
                    SetDevRumble(device, args.LargeMotor, args.SmallMotor, devIndex);
                };
                tempXbox.cont.FeedbackReceived += p;
                tempXbox.forceFeedbacksDict.Add(index, p);
            }
            else if (contType == OutContType.DS4)
            {
                DS4OutDevice tempDS4 = outDevice as DS4OutDevice;
                if (tempDS4.CanUseAwaitOutputBuffer)
                {
                    DS4OutDeviceExt.ReceivedOutBufferHandler processOutBuffAction = (DS4OutDeviceExt sender, byte[] reportData) =>
                    {
                        /*
                        //DS4OutputBufferData outputData = new DS4OutputBufferData();
                        DS4OutputBufferData outputData =
                            DS4OutDeviceExtras.ConvertOutputBufferArrayToStruct(reportData);

                        bool useRumble = false; bool useLight = false;
                        byte flashOn = 0; byte flashOff = 0;
                        DS4Color? color = null;

                        //Trace.WriteLine(string.Join(" ", reportData));

                        if ((outputData.featureFlags & DS4OutDevice.RUMBLE_FEATURE_FLAG) != 0)
                        {
                            useRumble = true;
                            device.setRumble(outputData.rightFastRumble, outputData.leftSlowRumble);
                            //SetDevRumble(device, devour[4], devour[5], devIndex);
                        }

                        if ((outputData.featureFlags & DS4OutDevice.LIGHTBAR_FEATURE_FLAG) != 0)
                        {
                            useLight = true;
                            color = new DS4Color(outputData.lightbarRedColor,
                                outputData.lightbarGreenColor,
                                outputData.lightbarBlueColor);
                        }
                        else
                        {
                            color = device.LightBarColor;
                        }

                        if ((outputData.featureFlags & DS4OutDevice.FLASH_FEATURE_FLAG) != 0)
                        {
                            useLight = true;
                            flashOn = outputData.flashOnDuration;
                            flashOff = outputData.flashOffDuration;
                        }
                        else
                        {
                            ref DS4LightbarState currentLight =
                                ref device.GetLightbarStateRef();

                            flashOn = currentLight.LightBarFlashDurationOn;
                            flashOff = currentLight.LightBarFlashDurationOff;
                        }

                        if (useLight)
                        {
                            DS4LightbarState lightState = new DS4LightbarState
                            {
                                LightBarColor = (DS4Color)color,
                                LightBarFlashDurationOn = flashOn,
                                LightBarFlashDurationOff = flashOff,
                            };
                            device.SetLightbarState(ref lightState);
                        }
                        //*/

                        //*
                        unchecked
                        {
                            //Trace.WriteLine($"INDEX: {devIndex}");
                            //Trace.WriteLine(string.Join(" ", reportData));
                            //Trace.WriteLine("");

                            bool useRumble = false; bool useLight = false;
                            byte flashOn = 0; byte flashOff = 0;
                            DS4Color? color = null;
                            if ((reportData[1] & DS4OutDevice.RUMBLE_FEATURE_FLAG) != 0)
                            {
                                useRumble = true;
                                if (Global.InverseRumbleMotors[devIndex])
                                    device.setRumble(reportData[5], reportData[4]);
                                else
                                    device.setRumble(reportData[4], reportData[5]);
                                //SetDevRumble(device, devour[4], devour[5], devIndex);
                            }

                            if ((reportData[1] & DS4OutDevice.LIGHTBAR_FEATURE_FLAG) != 0)
                            {
                                useLight = true;
                                color = new DS4Color(reportData[6],
                                    reportData[7],
                                    reportData[8]);
                            }
                            else
                            {
                                color = device.LightBarColor;
                            }

                            if ((reportData[1] & DS4OutDevice.FLASH_FEATURE_FLAG) != 0)
                            {
                                useLight = true;
                                flashOn = reportData[9];
                                flashOff = reportData[10];
                            }
                            else
                            {
                                ref DS4LightbarState currentLight =
                                    ref device.GetLightbarStateRef();

                                flashOn = currentLight.LightBarFlashDurationOn;
                                flashOff = currentLight.LightBarFlashDurationOff;
                            }

                            if (useLight)
                            {
                                DS4LightbarState lightState = new DS4LightbarState
                                {
                                    LightBarColor = (DS4Color)color,
                                    LightBarFlashDurationOn = flashOn,
                                    LightBarFlashDurationOff = flashOff,
                                };
                                device.SetLightbarState(ref lightState);
                            }
                        }
                        //*/
                    };

                    DS4OutDeviceExt tempDS4Ext = tempDS4 as DS4OutDeviceExt;
                    tempDS4Ext.ReceivedOutBuffer += processOutBuffAction;
                    tempDS4Ext.outBufferFeedbacksDict.TryAdd(index, processOutBuffAction);
                    tempDS4Ext.StartOutputBufferThread();
                }
            }
            //else if (contType == OutContType.DS4)
            //{
            //    DS4OutDevice tempDS4 = outDevice as DS4OutDevice;
            //    LightbarSettingInfo deviceLightbarSettingsInfo = Global.LightbarSettingsInfo[devIndex];

            //    Nefarius.ViGEm.Client.Targets.DualShock4FeedbackReceivedEventHandler p = (sender, args) =>
            //    {
            //        bool useRumble = false; bool useLight = false;
            //        byte largeMotor = args.LargeMotor;
            //        byte smallMotor = args.SmallMotor;
            //        //SetDevRumble(device, largeMotor, smallMotor, devIndex);
            //        DS4Color color = new DS4Color(args.LightbarColor.Red,
            //                args.LightbarColor.Green,
            //                args.LightbarColor.Blue);

            //        //Console.WriteLine("IN EVENT");
            //        //Console.WriteLine("Rumble ({0}, {1}) | Light ({2}, {3}, {4}) {5}",
            //        //    largeMotor, smallMotor, color.red, color.green, color.blue, DateTime.Now.ToString("hh:mm:ss.FFFF"));

            //        if (largeMotor != 0 || smallMotor != 0)
            //        {
            //            useRumble = true;
            //        }

            //        // Let games to control lightbar only when the mode is Passthru (otherwise DS4Windows controls the light)
            //        if (deviceLightbarSettingsInfo.Mode == LightbarMode.Passthru && (color.red != 0 || color.green != 0 || color.blue != 0))
            //        {
            //            useLight = true;
            //        }

            //        if (!useRumble && !useLight)
            //        {
            //            //Console.WriteLine("Fallback");
            //            if (device.LeftHeavySlowRumble != 0 || device.RightLightFastRumble != 0)
            //            {
            //                useRumble = true;
            //            }
            //            else if (deviceLightbarSettingsInfo.Mode == LightbarMode.Passthru &&
            //                (device.LightBarColor.red != 0 ||
            //                device.LightBarColor.green != 0 ||
            //                device.LightBarColor.blue != 0))
            //            {
            //                useLight = true;
            //            }
            //        }

            //        if (useRumble)
            //        {
            //            //Console.WriteLine("Perform rumble");
            //            SetDevRumble(device, largeMotor, smallMotor, devIndex);
            //        }

            //        if (useLight)
            //        {
            //            //Console.WriteLine("Change lightbar color");
            //            /*DS4HapticState haptics = new DS4HapticState
            //            {
            //                LightBarColor = color,
            //            };
            //            device.SetHapticState(ref haptics);
            //            */

            //            DS4LightbarState lightState = new DS4LightbarState
            //            {
            //                LightBarColor = color,
            //            };
            //            device.SetLightbarState(ref lightState);
            //        }

            //        //Console.WriteLine();
            //    };

            //    tempDS4.cont.FeedbackReceived += p;
            //    tempDS4.forceFeedbacksDict.Add(index, p);
            //}
        }

        public void RemoveOutFeedback(OutContType contType, OutputDevice outDevice, int inIdx)
        {
            if (contType == OutContType.X360)
            {
                Xbox360OutDevice tempXbox = outDevice as Xbox360OutDevice;
                tempXbox.RemoveFeedback(inIdx);
                //tempXbox.cont.FeedbackReceived -= tempXbox.forceFeedbackCall;
                //tempXbox.forceFeedbackCall = null;
            }
            else if (contType == OutContType.DS4)
            {
                DS4OutDevice tempDS4 = outDevice as DS4OutDevice;
                tempDS4.RemoveFeedback(inIdx);
            }
            //else if (contType == OutContType.DS4)
            //{
            //    DS4OutDevice tempDS4 = outDevice as DS4OutDevice;
            //    tempDS4.RemoveFeedback(inIdx);
            //    //tempDS4.cont.FeedbackReceived -= tempDS4.forceFeedbackCall;
            //    //tempDS4.forceFeedbackCall = null;
            //}
        }

        public void AttachNewUnboundOutDev(OutContType contType)
        {
            OutSlotDevice slotDevice = outputslotMan.FindOpenSlot();
            if (slotDevice != null &&
                slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.UnAttached)
            {
                OutputDevice outDevice = EstablishOutDevice(-1, contType);
                outputslotMan.DeferredPlugin(outDevice, -1, "", outputDevices, contType);
            }
        }

        public void AttachUnboundOutDev(OutSlotDevice slotDevice, OutContType contType)
        {
            if (slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.UnAttached &&
                slotDevice.CurrentInputBound == OutSlotDevice.InputBound.Unbound)
            {
                OutputDevice outDevice = EstablishOutDevice(-1, contType);
                outputslotMan.DeferredPlugin(outDevice, -1, "", outputDevices, contType);
            }
        }

        public void DetachUnboundOutDev(OutSlotDevice slotDevice)
        {
            if (slotDevice.CurrentInputBound == OutSlotDevice.InputBound.Unbound)
            {
                OutputDevice dev = slotDevice.OutputDevice;
                string tempType = dev.GetDeviceType();
                slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Unbound;
                outputslotMan.DeferredRemoval(dev, -1, outputDevices, false);
            }
        }

        public void PluginOutDev(int index, DS4Device device)
        {
            OutContType contType = Global.OutContType[index];
            StartupDiag($"PluginOutDev enter index={index} contType={contType} useDInputOnly={useDInputOnly[index]} profileDInputOnly={getDInputOnly(index)}");

            OutSlotDevice slotDevice = null;
            if (!getDInputOnly(index))
            {
                slotDevice = outputslotMan.FindExistUnboundSlotType(contType);
                StartupDiag($"PluginOutDev existingSlot index={index} found={slotDevice != null} slot={(slotDevice != null ? slotDevice.Index + 1 : 0)}");
            }

            if (useDInputOnly[index])
            {
                EnsureHidHideForVirtualOutput(index, device, contType);

                bool success = false;
                if (contType == OutContType.X360)
                {
                    activeOutDevType[index] = OutContType.X360;

                    if (slotDevice == null)
                    {
                        slotDevice = outputslotMan.FindOpenSlot();
                        StartupDiag($"PluginOutDev X360 openSlot index={index} found={slotDevice != null} slot={(slotDevice != null ? slotDevice.Index + 1 : 0)}");
                        if (slotDevice != null)
                        {
                            Xbox360OutDevice tempXbox = EstablishOutDevice(index, OutContType.X360)
                            as Xbox360OutDevice;
                            StartupDiag($"PluginOutDev X360 established index={index} null={tempXbox == null}");
                            //outputDevices[index] = tempXbox;

                            // Enable ViGem feedback callback handler only if lightbar/rumble data output is enabled (if those are disabled then no point enabling ViGem callback handler call)
                            if (Global.EnableOutputDataToDS4[index])
                            {
                                EstablishOutFeedback(index, OutContType.X360, tempXbox, device);

                                if (device.JointDeviceSlotNumber != -1)
                                {
                                    DS4Device tempDS4Device = DS4Controllers[device.JointDeviceSlotNumber];
                                    if (tempDS4Device != null)
                                    {
                                        EstablishOutFeedback(device.JointDeviceSlotNumber, OutContType.X360, tempXbox, tempDS4Device);
                                    }
                                }
                            }

                            StartupDiag($"PluginOutDev X360 DeferredPlugin begin index={index}");
                            outputslotMan.DeferredPlugin(tempXbox, index, $"{device.DisplayName} [{device.MacAddress}]", outputDevices, contType);
                            StartupDiag($"PluginOutDev X360 DeferredPlugin end index={index}");
                            //slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Bound;

                            success = true;
                        }
                        else
                        {
                            LogDebug("Failed. No open output slot found");
                        }
                    }
                    else
                    {
                        StartupDiag($"PluginOutDev X360 reusing slot index={index} slot={slotDevice.Index + 1}");
                        slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Bound;
                        Xbox360OutDevice tempXbox = slotDevice.OutputDevice as Xbox360OutDevice;

                        // Enable ViGem feedback callback handler only if lightbar/rumble data output is enabled (if those are disabled then no point enabling ViGem callback handler call)
                        if (Global.EnableOutputDataToDS4[index])
                        {
                            EstablishOutFeedback(index, OutContType.X360, tempXbox, device);

                            if (device.JointDeviceSlotNumber != -1)
                            {
                                DS4Device tempDS4Device = DS4Controllers[device.JointDeviceSlotNumber];
                                if (tempDS4Device != null)
                                {
                                    EstablishOutFeedback(device.JointDeviceSlotNumber, OutContType.X360, tempXbox, tempDS4Device);
                                }
                            }
                        }

                        outputDevices[index] = tempXbox;
                        slotDevice.CurrentType = contType;
                        success = true;
                    }

                    //tempXbox.Connect();
                    //LogDebug("X360 Controller #" + (index + 1) + " connected");
                }
                else if (contType == OutContType.DS4)
                {
                    activeOutDevType[index] = OutContType.DS4;
                    if (slotDevice == null)
                    {
                        slotDevice = outputslotMan.FindOpenSlot();
                        StartupDiag($"PluginOutDev DS4 openSlot index={index} found={slotDevice != null} slot={(slotDevice != null ? slotDevice.Index + 1 : 0)}");
                        if (slotDevice != null)
                        {
                            DS4OutDevice tempDS4 = EstablishOutDevice(index, OutContType.DS4)
                            as DS4OutDevice;
                            StartupDiag($"PluginOutDev DS4 established index={index} null={tempDS4 == null}");

                            // Enable ViGem feedback callback handler only if DS4 lightbar/rumble data output is enabled (if those are disabled then no point enabling ViGem callback handler call)
                            if (Global.EnableOutputDataToDS4[index])
                            {
                                EstablishOutFeedback(index, OutContType.DS4, tempDS4, device);

                                if (device.JointDeviceSlotNumber != -1)
                                {
                                    DS4Device tempDS4Device = DS4Controllers[device.JointDeviceSlotNumber];
                                    if (tempDS4Device != null)
                                    {
                                        EstablishOutFeedback(device.JointDeviceSlotNumber, OutContType.DS4, tempDS4, tempDS4Device);
                                    }
                                }
                            }

                            StartupDiag($"PluginOutDev DS4 DeferredPlugin begin index={index}");
                            outputslotMan.DeferredPlugin(tempDS4, index, $"{device.DisplayName} [{device.MacAddress}]", outputDevices, contType);
                            StartupDiag($"PluginOutDev DS4 DeferredPlugin end index={index}");
                            //slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Bound;

                            success = true;
                        }
                        else
                        {
                            LogDebug("Failed. No open output slot found");
                        }
                    }
                    else
                    {
                        StartupDiag($"PluginOutDev DS4 reusing slot index={index} slot={slotDevice.Index + 1}");
                        slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Bound;
                        DS4OutDevice tempDS4 = slotDevice.OutputDevice as DS4OutDevice;

                        // Enable ViGem feedback callback handler only if lightbar/rumble data output is enabled (if those are disabled then no point enabling ViGem callback handler call)
                        if (Global.EnableOutputDataToDS4[index])
                        {
                            EstablishOutFeedback(index, OutContType.DS4, tempDS4, device);

                            if (device.JointDeviceSlotNumber != -1)
                            {
                                DS4Device tempDS4Device = DS4Controllers[device.JointDeviceSlotNumber];
                                if (tempDS4Device != null)
                                {
                                    EstablishOutFeedback(device.JointDeviceSlotNumber, OutContType.DS4, tempDS4, tempDS4Device);
                                }
                            }
                        }

                        outputDevices[index] = tempDS4;
                        slotDevice.CurrentType = contType;
                        success = true;
                    }

                    //DS4OutDevice tempDS4 = new DS4OutDevice(vigemTestClient);
                    //DS4OutDevice tempDS4 = outputslotMan.AllocateController(OutContType.DS4, vigemTestClient)
                    //    as DS4OutDevice;
                    //outputDevices[index] = tempDS4;

                    //tempDS4.Connect();
                    //LogDebug("DS4 Controller #" + (index + 1) + " connected");
                }
                else if (ViiperOutDevice.IsViiperType(contType))
                {
                    activeOutDevType[index] = contType;
                    if (slotDevice == null)
                    {
                        slotDevice = outputslotMan.FindOpenSlot();
                        if (slotDevice != null)
                        {
                            OutputDevice tempViiper = EstablishOutDevice(index, contType);
                            outputslotMan.DeferredPlugin(tempViiper, index,
                                $"{device.DisplayName} [{device.MacAddress}]", outputDevices, contType);
                            success = true;
                        }
                        else
                        {
                            LogDebug("Failed. No open output slot found");
                        }
                    }
                    else
                    {
                        slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Bound;
                        outputDevices[index] = slotDevice.OutputDevice;
                        slotDevice.CurrentType = contType;
                        success = true;
                    }
                }

                // Need to check for possible ViGEmBus failure here
                if (success && slotDevice.OutputDevice != null)
                {
                    LogDebug($"Associated input controller #{index + 1} ({device.DisplayName}) to virtual {slotDevice.OutputDevice.GetDeviceType()} Controller in{(slotDevice.PermanentType != OutContType.None ? " permanent" : "")} output slot #{slotDevice.Index + 1}");
                    useDInputOnly[index] = false;
                    StartupDiag($"PluginOutDev success index={index} slot={slotDevice.Index + 1} output={slotDevice.OutputDevice.GetDeviceType()}");
                }
                else
                {
                    LogDebug("Failed. No output device was associated");
                    StartupDiag($"PluginOutDev failed index={index} success={success} slotNull={slotDevice == null} slotOutputNull={slotDevice?.OutputDevice == null}");
                }
            }
            else
            {
                StartupDiag($"PluginOutDev skipped index={index} useDInputOnly=false");
            }
        }

        public void UnplugOutDev(int index, DS4Device device, bool immediate = false, bool force = false)
        {
            if (!useDInputOnly[index])
            {
                try
                {
                    //OutContType contType = Global.OutContType[index];
                    OutputDevice dev = outputDevices[index];
                    OutSlotDevice slotDevice = outputslotMan.GetOutSlotDevice(dev);
                    if (dev != null && slotDevice != null)
                    {
                        string tempType = dev.GetDeviceType();
                        LogDebug($"Disassociated virtual {tempType} Controller in{(slotDevice.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Permanent ? " permanent" : "")} output slot #{slotDevice.Index + 1} from input controller #{index + 1} ({device.DisplayName})", false);

                        OutContType currentType = activeOutDevType[index];
                        outputDevices[index] = null;
                        activeOutDevType[index] = OutContType.None;
                        if ((slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached &&
                            slotDevice.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Dynamic) || force)
                        {
                            //slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Unbound;
                            outputslotMan.DeferredRemoval(dev, index, outputDevices, immediate);
                        }
                        else if (slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached)
                        {
                            slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Unbound;
                            dev.ResetState();
                            dev.RemoveFeedbacks();
                            //RemoveOutFeedback(currentType, dev);
                        }
                        //dev.Disconnect();
                        //LogDebug(tempType + " Controller # " + (index + 1) + " unplugged");
                    }
                }
                finally
                {
                    outputDevices[index] = null;
                    activeOutDevType[index] = OutContType.None;
                    useDInputOnly[index] = true;
                }
            }
        }

        public bool Start(bool showlog = true)
        {
            StartupDiag($"ControlService.Start enter showlog={showlog} running={running} inServiceTask={inServiceTask} admin={Global.IsAdministrator()}");
            inServiceTask = true;
            StartupDiag("ControlService.Start before StartViGEm");
            StartViGEm();
            StartupDiag($"ControlService.Start after StartViGEm client={vigemTestClient != null}");
            if (vigemTestClient != null)
            //if (x360Bus.Open() && x360Bus.Start())
            {
                // Initialize output KBM handler at start of ControlService
                StartupDiag("ControlService.Start before InitOutputKBMHandler");
                InitOutputKBMHandler();
                StartupDiag($"ControlService.Start after InitOutputKBMHandler handler={Global.outputKBMHandler?.GetFullDisplayName()}");

                if (showlog)
                    LogDebug(DS4WinWPF.Properties.Resources.Starting);

                Thread.Sleep(2000);

                bool runningAsAdmin = Global.IsAdministrator();
                if (Global.outputKBMHandler.GetIdentifier() != FakerInputHandler.IDENTIFIER && !runningAsAdmin)
                {
                    string helpURL = @"https://ryochan7.github.io/ds4windows-site/troubleshooting/kb-mouse-issues/#windows-not-responding-to-ds4ws-kb-m-commands-in-some-situations";
                    LogDebug($"Some applications may block controller inputs. (Windows UAC Conflictions). Please go to {helpURL} for more information and workarounds.");
                }

                LogDebug($"Using output KB+M handler: {Global.outputKBMHandler.GetFullDisplayName()}");
                LogDebug($"Connection to ViGEmBus {Global.vigembusVersion} established");

                DS4Devices.isExclusiveMode = getUseExclusiveMode(); //Re-enable Exclusive Mode

                StartupDiag($"UpdateHidHiddenAttributes begin exclusive={DS4Devices.isExclusiveMode}");
                UpdateHidHiddenAttributes();
                StartupDiag("UpdateHidHiddenAttributes end");

                if (Global.openRGBSyncEnabled)
                {
                    StartupDiag($"OpenRGB start begin port={Global.openRGBServerPort}");
                    bool openRGBStarted = OpenRGBServer.Instance.Start(Global.openRGBServerPort);
                    StartupDiag($"OpenRGB start end started={openRGBStarted}");
                    if (showlog)
                        LogDebug(openRGBStarted
                            ? $"OpenRGB server listening on port {Global.openRGBServerPort}"
                            : $"OpenRGB server could not bind to port {Global.openRGBServerPort} - lightbar will use profile colour");
                }

                if (showlog)
                {
                    LogDebug(DS4WinWPF.Properties.Resources.SearchingController);
                    LogDebug(DS4Devices.isExclusiveMode ? DS4WinWPF.Properties.Resources.UsingExclusive : DS4WinWPF.Properties.Resources.UsingShared);
                }

                if (isUsingOSCServer() && oscListener == null)
                {
                    StartupDiag("OSC listener start begin");
                    ChangeOSCListenerStatus(true);
                    StartupDiag("OSC listener start requested");
                }

                if (isUsingOSCSender() && oscSender == null)
                {
                    StartupDiag("OSC sender start begin");
                    ChangeOSCSenderStatus(true);
                    StartupDiag("OSC sender start requested");
                }

                if (isUsingUDPServer() && _udpServer == null)
                {
                    StartupDiag("UDP change-status start begin");
                    ChangeUDPStatus(true, false);
                    while (udpChangeStatus == true)
                    {
                        Thread.SpinWait(500);
                    }
                    StartupDiag("UDP change-status start end");
                }

                try
                {
                    loopControllers = true;
                    StartupDiag("AssignInitialDevices begin");
                    AssignInitialDevices();
                    StartupDiag("AssignInitialDevices end");

                    StartupDiag("DS4Devices.findControllers dispatch begin");
                    eventDispatcher.Invoke(() =>
                    {
                        DS4Devices.findControllers();
                    });
                    StartupDiag("DS4Devices.findControllers dispatch end");

                    IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
                    int numControllers = devices.Count();
                    StartupDiag($"DS4Devices.getDS4Controllers count={numControllers}");
                    activeControllers = numControllers;
                    DS4LightBar.defaultLight = false;
                    int i = 0;
                    InputDevices.JoyConDevice tempPrimaryJoyDev = null;
                    for (var devEnum = devices.GetEnumerator();
                        devEnum.MoveNext() && loopControllers; i++)
                    {
                        DS4Device device = devEnum.Current;
                        StartupDiag($"Prepare controller loop index={i} type={device.DeviceType} display={device.DisplayName} mac={device.MacAddress} conn={device.ConnectionType} synced={device.isSynced()} primary={device.PrimaryDevice}");

                        StartupDiag($"BeginPrepareConnectedInputController begin index={i}");
                        BeginPrepareConnectedInputController(device, showlog: true);
                        StartupDiag($"BeginPrepareConnectedInputController end index={i}");

                        if (deviceOptions.JoyConDeviceOpts.LinkedMode == JoyConDeviceOptions.LinkMode.Joined)
                        {
                            if ((device.DeviceType == InputDevices.InputDeviceType.JoyConL ||
                                device.DeviceType == InputDevices.InputDeviceType.JoyConR) && device.PerformStateMerge)
                            {
                                if (tempPrimaryJoyDev == null)
                                {
                                    tempPrimaryJoyDev = device as InputDevices.JoyConDevice;
                                }
                                else
                                {
                                    InputDevices.JoyConDevice currentJoyDev = device as InputDevices.JoyConDevice;
                                    tempPrimaryJoyDev.JointDevice = currentJoyDev;
                                    currentJoyDev.JointDevice = tempPrimaryJoyDev;

                                    tempPrimaryJoyDev.JointState = currentJoyDev.JointState;

                                    InputDevices.JoyConDevice parentJoy = tempPrimaryJoyDev;
                                    tempPrimaryJoyDev.Removal += (sender, args) =>
                                    {
                                        currentJoyDev.JointDevice = null;
                                    };
                                    currentJoyDev.Removal += (sender, args) =>
                                    {
                                        parentJoy.JointDevice = null;
                                    };

                                    tempPrimaryJoyDev = null;
                                }
                            }
                        }

                        DS4Controllers[i] = device;
                        device.DeviceSlotNumber = i;
                        StartupDiag($"PrepareConnectedInputControllerSettingEvents begin index={i}");
                        PrepareConnectedInputControllerSettingEvents(numControllers, device, index: i);
                        StartupDiag($"PrepareConnectedInputControllerSettingEvents end index={i}");

                        if (i >= CURRENT_DS4_CONTROLLER_LIMIT) // out of Xinput devices!
                            break;
                    }
                }
                catch (Exception e)
                {
                    StartupDiag($"ControlService.Start managed exception {e.GetType().Name}: {e.Message}");
                    LogDebug(e.Message, true);
                    AppLogger.LogToTray(e.Message, true);
                }

                StartupDiag("ControlService.Start setting running=true");
                running = true;
                StartGameBarProfileTimer();

                if (_udpServer != null)
                {
                    //var UDP_SERVER_PORT = 26760;
                    var UDP_SERVER_PORT = Global.getUDPServerPortNum();
                    var UDP_SERVER_LISTEN_ADDRESS = Global.getUDPServerListenAddress();

                    try
                    {
                        StartupDiag($"UDP server Start begin address={UDP_SERVER_LISTEN_ADDRESS} port={UDP_SERVER_PORT}");
                        _udpServer.Start(UDP_SERVER_PORT, UDP_SERVER_LISTEN_ADDRESS);
                        LogDebug($"UDP server listening on address {UDP_SERVER_LISTEN_ADDRESS} port {UDP_SERVER_PORT}");
                        StartupDiag("UDP server Start end");
                    }
                    catch (System.Net.Sockets.SocketException ex)
                    {
                        StartupDiag($"UDP server Start exception {ex.SocketErrorCode}: {ex.Message}");
                        var errMsg = string.Format("Couldn't start UDP server on address {0}:{1}, outside applications won't be able to access pad data ({2})", UDP_SERVER_LISTEN_ADDRESS, UDP_SERVER_PORT, ex.SocketErrorCode);

                        LogDebug(errMsg, true);
                        AppLogger.LogToTray(errMsg, true, true);
                    }
                }
            }
            else
            {
                StartupDiag("ControlService.Start no ViGEm client");
                string logMessage = string.Empty;
                if (!vigemInstalled)
                {
                    logMessage = "ViGEmBus is not installed";
                }
                else if (!Global.IsRunningSupportedViGEmBus())
                {
                    logMessage = string.Format("Unsupported ViGEmBus found ({0}). Please install at least ViGEmBus 1.17.333.0", Global.vigembusVersion);
                }
                else
                {
                    logMessage = "Could not connect to ViGEmBus. Please check the status of the System device in Device Manager and if Visual C++ 2017 Redistributable is installed.";
                }

                LogDebug(logMessage);
                AppLogger.LogToTray(logMessage);
            }

            inServiceTask = false;
            runHotPlug = true;
            StartupDiag("ControlService.Start before ServiceStarted events");
            ServiceStarted?.Invoke(this, EventArgs.Empty);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            StartupDiag("ControlService.Start after RunningChanged");
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = MainWindow.ProcessPriorityClasses[Global.ProcessPriority];
            StartupDiag($"ControlService.Start exit priority={process.PriorityClass}");
            return true;
        }

        private void PrepareDevUDPMotion(DS4Device device, int index)
        {
            int tempIdx = index;
            DS4Device.ReportHandler<EventArgs> tempEvnt = (sender, args) =>
            {
                DualShockPadMeta padDetail = new DualShockPadMeta();
                GetPadDetailForIdx(tempIdx, ref padDetail);
                DS4State stateForUdp = TempState[tempIdx];

                CurrentState[tempIdx].CopyTo(stateForUdp);
                if (Global.IsUsingUDPServerSmoothing())
                {
                    if (stateForUdp.elapsedTime == 0)
                    {
                        // No timestamp was found. Exit out of routine
                        return;
                    }

                    double rate = 1.0 / stateForUdp.elapsedTime;
                    OneEuroFilter3D accelFilter = udpEuroPairAccel[tempIdx];
                    stateForUdp.Motion.accelXG = accelFilter.axis1Filter.Filter(stateForUdp.Motion.accelXG, rate);
                    stateForUdp.Motion.accelYG = accelFilter.axis2Filter.Filter(stateForUdp.Motion.accelYG, rate);
                    stateForUdp.Motion.accelZG = accelFilter.axis3Filter.Filter(stateForUdp.Motion.accelZG, rate);

                    OneEuroFilter3D gyroFilter = udpEuroPairGyro[tempIdx];
                    stateForUdp.Motion.angVelYaw = gyroFilter.axis1Filter.Filter(stateForUdp.Motion.angVelYaw, rate);
                    stateForUdp.Motion.angVelPitch = gyroFilter.axis2Filter.Filter(stateForUdp.Motion.angVelPitch, rate);
                    stateForUdp.Motion.angVelRoll = gyroFilter.axis3Filter.Filter(stateForUdp.Motion.angVelRoll, rate);
                }

                _udpServer?.NewReportIncoming(ref padDetail, stateForUdp, udpOutBuffers[tempIdx]);
            };

            device.MotionEvent = tempEvnt;
            device.Report += tempEvnt;
        }

        private void CheckQuickCharge(object sender, EventArgs e)
        {
            DS4Device device = sender as DS4Device;
            if (device.ConnectionType == ConnectionType.BT && getQuickCharge() &&
                device.Charging)
            {
                // Set disconnect flag here. Later Hotplug event will check
                // for presence of flag and remove the device then
                device.ReadyQuickChargeDisconnect = true;
            }
        }

        public void PrepareAbort()
        {
            for (int i = 0, arlength = DS4Controllers.Length; i < arlength; i++)
            {
                DS4Device tempDevice = DS4Controllers[i];
                if (tempDevice != null)
                {
                    tempDevice.PrepareAbort();
                }
            }
        }

        public bool Stop(bool showlog = true, bool immediateUnplug = false, bool disposeViGEm = true)
        {
            StartupDiag($"ControlService.Stop enter showlog={showlog} immediate={immediateUnplug} disposeViGEm={disposeViGEm} running={running}");
            if (running)
            {
                if (OpenRGBServer.Instance.IsRunning)
                {
                    StartupDiag("ControlService.Stop OpenRGB stop begin");
                    OpenRGBServer.Instance.Stop();
                    StartupDiag("ControlService.Stop OpenRGB stop end");
                }

                running = false;
                runHotPlug = false;
                inServiceTask = true;
                StopGameBarProfileTimer();
                StartupDiag("ControlService.Stop PreServiceStop begin");
                PreServiceStop?.Invoke(this, EventArgs.Empty);
                StartupDiag("ControlService.Stop PreServiceStop end");

                if (showlog)
                    LogDebug(DS4WinWPF.Properties.Resources.StoppingX360);

                LogDebug("Closing connection to ViGEmBus");

                bool anyUnplugged = false;
                for (int i = 0, arlength = DS4Controllers.Length; i < arlength; i++)
                {
                    DS4Device tempDevice = DS4Controllers[i];
                    if (tempDevice != null)
                    {
                        StartupDiag($"ControlService.Stop controller loop index={i} display={tempDevice.DisplayName} mac={tempDevice.MacAddress} conn={tempDevice.ConnectionType} charging={tempDevice.isCharging()}");
                        if ((DCBTatStop && !tempDevice.isCharging()) || suspending)
                        {
                            if (tempDevice.getConnectionType() == ConnectionType.BT)
                            {
                                tempDevice.StopUpdate();
                                tempDevice.DisconnectBT(true);
                            }
                            else if (tempDevice.getConnectionType() == ConnectionType.SONYWA)
                            {
                                // Controller disconnect will complete on next attempted read.
                                // Do not use StopUpdate here
                                tempDevice.DisconnectDongle(true);
                            }
                            else
                            {
                                tempDevice.StopUpdate();
                            }
                        }
                        else
                        {
                            if (!immediateUnplug)
                            {
                                DS4LightBar.forcelight[i] = false;
                                DS4LightBar.forcedFlash[i] = 0;
                                DS4LightBar.defaultLight = true;
                                DS4LightBar.updateLightBar(DS4Controllers[i], i);
                            }

                            tempDevice.IsRemoved = true;
                            tempDevice.StopUpdate();
                            DS4Devices.RemoveDevice(tempDevice);
                            Thread.Sleep(50);
                        }

                        CurrentState[i].Battery = PreviousState[i].Battery = 0; // Reset for the next connection's initial status change.
                        OutputDevice tempout = outputDevices[i];
                        if (tempout != null)
                        {
                            StartupDiag($"ControlService.Stop UnplugOutDev begin index={i} type={tempout.GetDeviceType()}");
                            UnplugOutDev(i, tempDevice, immediate: immediateUnplug, force: true);
                            StartupDiag($"ControlService.Stop UnplugOutDev end index={i}");
                            anyUnplugged = true;
                        }

                        //outputDevices[i] = null;
                        //useDInputOnly[i] = true;
                        //Global.activeOutDevType[i] = OutContType.None;
                        useDInputOnly[i] = true;
                        DS4Controllers[i] = null;
                        oscState[i] = new DS4State();
                        touchPad[i] = null;
                        lag[i] = false;
                        inWarnMonitor[i] = false;
                    }
                }

                if (showlog)
                    LogDebug(DS4WinWPF.Properties.Resources.StoppingDS4);

                StartupDiag("ControlService.Stop DualSenseAudio dispose begin");
                dualSenseAudioPassthrough.Dispose();
                StartupDiag("ControlService.Stop DualSenseAudio dispose end");
                StartupDiag("ControlService.Stop DualSenseMicrophone dispose begin");
                dualSenseMicrophonePassthrough.Dispose();
                StartupDiag("ControlService.Stop DualSenseMicrophone dispose end");
                StartupDiag("ControlService.Stop DS4Devices.stopControllers begin");
                DS4Devices.stopControllers();
                StartupDiag("ControlService.Stop DS4Devices.stopControllers end");
                slotManager.ClearControllerList();

                if (oscListener != null)
                {
                    ChangeOSCListenerStatus(false);
                }

                if (oscSender != null)
                {
                    ChangeOSCSenderStatus(false);
                }

                if (_udpServer != null)
                {
                    StartupDiag("ControlService.Stop UDP stop begin");
                    ChangeUDPStatus(false);
                    StartupDiag("ControlService.Stop UDP stop requested");
                }

                if (showlog)
                    LogDebug(DS4WinWPF.Properties.Resources.StoppedDS4Windows);

                Stopwatch outputQueueWait = Stopwatch.StartNew();
                while (outputslotMan.RunningQueue && outputQueueWait.ElapsedMilliseconds < 2000)
                {
                    Thread.Sleep(1);
                }

                if (outputslotMan.RunningQueue)
                {
                    StartupDiag("ControlService.Stop timed out waiting for output slot queue");
                }

                StartupDiag("ControlService.Stop outputslotMan.Stop begin");
                outputslotMan.Stop(true);
                StartupDiag("ControlService.Stop outputslotMan.Stop end");

                if (anyUnplugged)
                {
                    Thread.Sleep(OutputSlotManager.DELAY_TIME);
                }

                if (disposeViGEm)
                {
                    StopViGEm();
                }
                else
                {
                    StartupDiag("ControlService.Stop skipping ViGEm Dispose during app exit");
                    vigemTestClient = null;
                }

                // Disconnect from KBM system when stopping ControlService
                StartupDiag($"ControlService.Stop outputKBM Disconnect begin handler={outputKBMHandler?.GetFullDisplayName()}");
                LogDebug($"Closing connection to output handler {outputKBMHandler.GetDisplayName()}");
                outputKBMHandler.Disconnect();
                StartupDiag("ControlService.Stop outputKBM Disconnect end");
                inServiceTask = false;
                activeControllers = 0;
            }

            runHotPlug = false;
            ReleaseHidHideManagedDevices();
            StartupDiag("ControlService.Stop before stopped events");
            ServiceStopped?.Invoke(this, EventArgs.Empty);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            StartupDiag("ControlService.Stop exit");
            return true;
        }

        public bool HotPlug()
        {
            if (running)
            {
                inServiceTask = true;
                loopControllers = true;
                ReconcileDisconnectedControllers();
                eventDispatcher.Invoke(() =>
                {
                    DS4Devices.findControllers();
                });

                IEnumerable<DS4Device> devices = DS4Devices.getDS4Controllers();
                int numControllers = devices.Count();
                activeControllers = numControllers;
                InputDevices.JoyConDevice tempPrimaryJoyDev = null;
                InputDevices.JoyConDevice tempSecondaryJoyDev = null;

                if (deviceOptions.JoyConDeviceOpts.LinkedMode == JoyConDeviceOptions.LinkMode.Joined)
                {
                    tempPrimaryJoyDev = devices.Where(d =>
                        (d.DeviceType == InputDevices.InputDeviceType.JoyConL || d.DeviceType == InputDevices.InputDeviceType.JoyConR)
                         && d.PrimaryDevice && d.JointDeviceSlotNumber == -1).FirstOrDefault() as InputDevices.JoyConDevice;

                    tempSecondaryJoyDev = devices.Where(d =>
                        (d.DeviceType == InputDevices.InputDeviceType.JoyConL || d.DeviceType == InputDevices.InputDeviceType.JoyConR)
                        && !d.PrimaryDevice && d.JointDeviceSlotNumber == -1).FirstOrDefault() as InputDevices.JoyConDevice;
                }

                for (var devEnum = devices.GetEnumerator(); devEnum.MoveNext() && loopControllers;)
                {
                    DS4Device device = devEnum.Current;

                    if (device.isDisconnectingStatus())
                        continue;

                    // Use local method rather than Func
                    bool checkAlreadyExists()
                    {
                        for (int Index = 0, arlength = DS4Controllers.Length; Index < arlength; Index++)
                        {
                            if (DS4Controllers[Index] != null &&
                                DS4Controllers[Index].getMacAddress() == device.getMacAddress())
                            {
                                device.CheckControllerNumDeviceSettings(numControllers);
                                return true;
                            }
                        }

                        return false;
                    }

                    if (checkAlreadyExists())
                    {
                        continue;
                    }

                    for (int Index = 0, arlength = DS4Controllers.Length;
                        Index < arlength && Index < CURRENT_DS4_CONTROLLER_LIMIT; Index++)
                    {
                        if (DS4Controllers[Index] == null)
                        {
                            BeginPrepareConnectedInputController(device);

                            if (deviceOptions.JoyConDeviceOpts.LinkedMode == JoyConDeviceOptions.LinkMode.Joined)
                            {
                                if ((device.DeviceType == InputDevices.InputDeviceType.JoyConL ||
                                    device.DeviceType == InputDevices.InputDeviceType.JoyConR) && device.PerformStateMerge)
                                {
                                    if (device.PrimaryDevice &&
                                        tempSecondaryJoyDev != null)
                                    {
                                        InputDevices.JoyConDevice currentJoyDev = device as InputDevices.JoyConDevice;
                                        tempSecondaryJoyDev.JointDevice = currentJoyDev;
                                        currentJoyDev.JointDevice = tempSecondaryJoyDev;

                                        tempSecondaryJoyDev.JointState = currentJoyDev.JointState;

                                        InputDevices.JoyConDevice secondaryJoy = tempSecondaryJoyDev;
                                        secondaryJoy.Removal += (sender, args) =>
                                        {
                                            currentJoyDev.JointDevice = null;
                                        };
                                        currentJoyDev.Removal += (sender, args) =>
                                        {
                                            secondaryJoy.JointDevice = null;
                                        };

                                        tempSecondaryJoyDev = null;
                                        tempPrimaryJoyDev = null;
                                    }
                                    else if (!device.PrimaryDevice &&
                                        tempPrimaryJoyDev != null)
                                    {
                                        InputDevices.JoyConDevice currentJoyDev = device as InputDevices.JoyConDevice;
                                        tempPrimaryJoyDev.JointDevice = currentJoyDev;
                                        currentJoyDev.JointDevice = tempPrimaryJoyDev;

                                        tempPrimaryJoyDev.JointState = currentJoyDev.JointState;

                                        InputDevices.JoyConDevice parentJoy = tempPrimaryJoyDev;
                                        tempPrimaryJoyDev.Removal += (sender, args) =>
                                        {
                                            currentJoyDev.JointDevice = null;
                                        };
                                        currentJoyDev.Removal += (sender, args) =>
                                        {
                                            parentJoy.JointDevice = null;
                                        };

                                        tempPrimaryJoyDev = null;
                                    }
                                }
                            }

                            DS4Controllers[Index] = device;
                            device.DeviceSlotNumber = Index;
                            PrepareConnectedInputControllerSettingEvents(numControllers, device, Index);

                            HotplugController?.Invoke(this, device, Index);
                            break;
                        }
                    }
                }

                inServiceTask = false;
            }

            return true;
        }

        private void PrepareConnectedInputControllerSettingEvents(int numControllers, DS4Device device, int index)
        {
            StartupDiag($"Controller prep begin index={index} numControllers={numControllers} display={device.DisplayName} mac={device.MacAddress} type={device.DeviceType}");
            StartupDiag($"RefreshExtrasButtons begin index={index}");
            Global.RefreshExtrasButtons(index, GetKnownExtraButtons(device));
            StartupDiag($"RefreshExtrasButtons end index={index}");
            StartupDiag($"LoadControllerConfigs begin index={index}");
            Global.LoadControllerConfigs(device);
            StartupDiag($"LoadControllerConfigs end index={index}");
            StartupDiag($"device.LoadStoreSettings begin index={index}");
            device.LoadStoreSettings();
            StartupDiag($"device.LoadStoreSettings end index={index}");
            StartupDiag($"CheckControllerNumDeviceSettings begin index={index}");
            device.CheckControllerNumDeviceSettings(numControllers);
            StartupDiag($"CheckControllerNumDeviceSettings end index={index}");

            slotManager.AddController(device, index);
            if (isUsingOSCSender())
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/plug", 1));
            }
            device.Removal += this.On_DS4Removal;
            device.Removal += DS4Devices.On_Removal;
            device.SyncChange += this.On_SyncChange;
            device.SyncChange += DS4Devices.UpdateSerial;
            device.SerialChange += this.On_SerialChange;
            device.ChargingChanged += CheckQuickCharge;

            StartupDiag($"TouchPad create begin index={index}");
            touchPad[index] = new Mouse(index, device);
            StartupDiag($"TouchPad create end index={index}");
            bool profileLoaded = false;
            bool useAutoProfile = useTempProfile[index];
            if (!useAutoProfile)
            {
                if (device.isValidSerial() && containsLinkedProfile(device.getMacAddress()))
                {
                    ProfilePath[index] = getLinkedProfile(device.getMacAddress());
                    Global.linkedProfileCheck[index] = true;
                }
                else
                {
                    ProfilePath[index] = OlderProfilePath[index];
                    Global.linkedProfileCheck[index] = false;
                }

                // Now attempt to load requested profile and settings
                StartupDiag($"LoadProfile begin index={index} profile=\"{ProfilePath[index]}\" linked={Global.linkedProfileCheck[index]}");
                profileLoaded = LoadProfile(index, false, this, false, false);
                StartupDiag($"LoadProfile end index={index} loaded={profileLoaded} profile=\"{ProfilePath[index]}\" dinputOnly={getDInputOnly(index)} outType={Global.OutContType[index]}");
            }
            else
            {
                StartupDiag($"LoadProfile skipped for auto/temp profile index={index} tempProfile=\"{tempprofilename[index]}\"");
            }

            if (profileLoaded || useAutoProfile)
            {
                device.LightBarColor = getMainColor(index);

                if (!getDInputOnly(index) && device.isSynced())
                {
                    if (device.PrimaryDevice)
                    {
                        StartupDiag($"PluginOutDev begin index={index} outType={Global.OutContType[index]}");
                        PluginOutDev(index, device);
                        StartupDiag($"PluginOutDev end index={index} useDInputOnly={useDInputOnly[index]} activeOut={activeOutDevType[index]} outDev={outputDevices[index]?.GetDeviceType() ?? "null"}");
                    }
                    else if (device.JointDeviceSlotNumber != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                    {
                        int otherIdx = device.JointDeviceSlotNumber;
                        OutputDevice tempOutDev = outputDevices[otherIdx];
                        if (tempOutDev != null)
                        {
                            OutContType tempConType = activeOutDevType[otherIdx];
                            EstablishOutFeedback(index, tempConType, tempOutDev, device);
                            outputDevices[index] = tempOutDev;
                            Global.activeOutDevType[index] = tempConType;
                        }
                    }
                }
                else
                {
                    useDInputOnly[index] = true;
                    Global.activeOutDevType[index] = OutContType.None;
                }

                if (device.PrimaryDevice && device.OutputMapGyro)
                {
                    StartupDiag($"TouchPadOn begin index={index}");
                    TouchPadOn(index, device);
                    StartupDiag($"TouchPadOn end index={index}");
                }
                else if (device.JointDeviceSlotNumber != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                {
                    int otherIdx = device.JointDeviceSlotNumber;
                    DS4Device tempDev = DS4Controllers[otherIdx];
                    if (tempDev != null)
                    {
                        int mappedIdx = tempDev.PrimaryDevice ? otherIdx : index;
                        DS4Device gyroDev = device.OutputMapGyro ? device : (tempDev.OutputMapGyro ? tempDev : null);
                        if (gyroDev != null)
                        {
                            TouchPadOn(mappedIdx, gyroDev);
                        }
                    }
                }

                StartupDiag($"CheckProfileOptions begin index={index}");
                CheckProfileOptions(index, device);
                StartupDiag($"CheckProfileOptions end index={index}");
                StartupDiag($"SetupInitialHookEvents begin index={index}");
                SetupInitialHookEvents(index, device);
                StartupDiag($"SetupInitialHookEvents end index={index}");
            }
            else
            {
                StartupDiag($"Controller prep profile not loaded index={index} profile=\"{ProfilePath[index]}\"");
            }

            int tempIdx = index;
            device.Report += (sender, e) =>
            {
                this.On_Report(sender, e, tempIdx);
            };
            StartupDiag($"Report hook added index={index}");

            if (_udpServer != null && index < UdpServer.NUMBER_SLOTS)
            {
                StartupDiag($"PrepareDevUDPMotion begin index={index}");
                PrepareDevUDPMotion(device, tempIdx);
                StartupDiag($"PrepareDevUDPMotion end index={index}");
            }

            StartupDiag($"device.StartUpdate begin index={index}");
            device.StartUpdate();
            StartupDiag($"device.StartUpdate end index={index}");
            StartupDiag($"Controller prep end index={index}");
        }

        private void BeginPrepareConnectedInputController(DS4Device device, bool showlog = false)
        {
            if (DS4Devices.isExclusiveMode && EnsureHidHideSessionForDevice(device))
            {
                ChangeExclusiveStatus(device);
            }
            else if (hidDeviceHidingEnabled && CheckAffected(device))
            {
                ChangeExclusiveStatus(device);
            }

            //Task task = new Task(() => { Thread.Sleep(5); WarnExclusiveModeFailure(device); });
            //task.Start();

            PrepareDS4DeviceSettingHooks(device);
        }

        public void ResetUdpSmoothingFilters(int idx)
        {
            if (idx < UdpServer.NUMBER_SLOTS)
            {
                OneEuroFilter3D temp = udpEuroPairAccel[idx] = new OneEuroFilter3D();
                temp.SetFilterAttrs(Global.UDPServerSmoothingMincutoff, Global.UDPServerSmoothingBeta);

                temp = udpEuroPairGyro[idx] = new OneEuroFilter3D();
                temp.SetFilterAttrs(Global.UDPServerSmoothingMincutoff, Global.UDPServerSmoothingBeta);
            }
        }

        private void ChangeUdpSmoothingAttrs(object sender, EventArgs e)
        {
            for (int i = 0; i < udpEuroPairAccel.Length; i++)
            {
                OneEuroFilter3D temp = udpEuroPairAccel[i];
                temp.SetFilterAttrs(Global.UDPServerSmoothingMincutoff, Global.UDPServerSmoothingBeta);
            }

            for (int i = 0; i < udpEuroPairGyro.Length; i++)
            {
                OneEuroFilter3D temp = udpEuroPairGyro[i];
                temp.SetFilterAttrs(Global.UDPServerSmoothingMincutoff, Global.UDPServerSmoothingBeta);
            }
        }

        public void CheckProfileOptions(int ind, DS4Device device, bool startUp = false)
        {
            EnsureVirtualMouseForStickMouseProfile(ind);

            device.ModifyFeatureSetFlag(VidPidFeatureSet.NoOutputData, !getEnableOutputDataToDS4(ind));
            if (!getEnableOutputDataToDS4(ind))
                LogDebug("Output data to DS4 disabled. Lightbar and rumble events are not written to DS4 gamepad. If the gamepad is connected over BT then IdleDisconnect option is recommended to let DS4Windows to close the connection after long period of idling.");

            device.setIdleTimeout(getIdleDisconnectTimeout(ind));
            device.setBTPollRate(getBTPollRate(ind));

            touchPad[ind].ResetTrackAccel(getTrackballFriction(ind));
            touchPad[ind].ResetToggleGyroModes();

            //Global.TouchOutMode[ind] = TouchpadOutMode.MouseJoystick;
            touchPad[ind].PostSetup();

            if (Global.L2OutputSettings[ind].TrigEffectSettings.maxValue == 0)
            {
                Global.L2OutputSettings[ind].TrigEffectSettings.maxValue = (byte)(Math.Max(Global.L2ModInfo[ind].maxOutput, Global.L2ModInfo[ind].maxZone) / 100.0 * 255);
            }

            if (Global.R2OutputSettings[ind].TrigEffectSettings.maxValue == 0)
            {
                Global.R2OutputSettings[ind].TrigEffectSettings.maxValue = (byte)(Math.Max(Global.R2ModInfo[ind].maxOutput, Global.R2ModInfo[ind].maxZone) / 100.0 * 255);
            }

            device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[ind].TriggerEffect,
                Global.L2OutputSettings[ind].TrigEffectSettings);
            device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[ind].TriggerEffect,
                Global.R2OutputSettings[ind].TrigEffectSettings);

            device.RumbleAutostopTime = getRumbleAutostopTime(ind);
            device.setRumble(0, 0);
            device.LightBarColor = Global.getMainColor(ind);

            // DualSense specific profile settings
            if (device is InputDevices.DualSenseDevice dualsense)
            {
                switch (DualSenseRumbleEmulationMode[ind])
                {
                    case InputDevices.DualSenseDevice.RumbleEmulationMode.Disabled:
                        dualsense.UseRumble = false;
                        dualsense.UseAccurateRumble = false;
                        break;
                    case InputDevices.DualSenseDevice.RumbleEmulationMode.Legacy:
                        dualsense.UseRumble = true;
                        dualsense.UseAccurateRumble = false;
                        break;
                    case InputDevices.DualSenseDevice.RumbleEmulationMode.Accurate:
                    default:
                        dualsense.UseRumble = true;
                        dualsense.UseAccurateRumble = true;
                        break;
                }
                dualsense.HapticPowerLevel = DualSenseHapticPowerLevel[ind];
                dualsense.EnableSpeakerOutput = DualSenseEnableSpeakerOutput[ind];
                dualsense.SpeakerVolume = DualSenseSpeakerVolume[ind];
                dualsense.HeadphoneVolume = DualSenseHeadphoneVolume[ind];
                dualsense.MicrophoneVolume = DualSenseMicrophoneVolume[ind];

                if (DualSenseEnableSpeakerOutput[ind])
                {
                    dualSenseAudioPassthrough.Start(ind, dualsense, DualSenseSpeakerVolume[ind],
                        DualSenseHeadphoneVolume[ind], DualSenseHeadsetPluggedIn[ind],
                        DualSenseAudioCaptureEndpointId[ind],
                        DualSenseAudioSpeakerEndpointId[ind]);
                }
                else
                {
                    dualSenseAudioPassthrough.Stop(ind);
                }

                bool dualSenseMicrophoneRuntimeAvailable =
                    InputDevices.DualSenseDevice.BluetoothMicrophoneInputTransportAvailable;
                bool useViiperDualSenseMicrophone =
                    DualSenseEnableMicrophonePassthrough[ind] &&
                    dualSenseMicrophoneRuntimeAvailable &&
                    (Global.OutContType[ind] == OutContType.ViiperDualSense ||
                    Global.OutContType[ind] == OutContType.ViiperDualSenseEdge);

                if (DualSenseEnableMicrophonePassthrough[ind] &&
                    dualSenseMicrophoneRuntimeAvailable &&
                    !useViiperDualSenseMicrophone)
                {
                    dualSenseMicrophonePassthrough.Start(DualSenseMicrophoneVolume[ind],
                        DualSenseMicrophoneCaptureEndpointId[ind],
                        DualSenseMicrophoneOutputEndpointId[ind]);
                }
                else
                {
                    dualSenseMicrophonePassthrough.Stop();
                }
            }
            else
            {
                dualSenseAudioPassthrough.Stop(ind);
                dualSenseMicrophonePassthrough.Stop();
            }

            if (!startUp)
            {
                CheckLauchProfileOption(ind, device);
            }
        }

        private void CheckLauchProfileOption(int ind, DS4Device device)
        {
            string programPath = LaunchProgram[ind];
            if (programPath != string.Empty)
            {
                Process[] localAll = Process.GetProcesses();
                bool procFound = false;
                for (int procInd = 0, procsLen = localAll.Length; !procFound && procInd < procsLen; procInd++)
                {
                    try
                    {
                        string temp = localAll[procInd].MainModule.FileName;
                        if (temp == programPath)
                        {
                            procFound = true;
                        }
                    }
                    // Ignore any process for which this information
                    // is not exposed
                    catch { }
                }

                if (!procFound)
                {
                    Task processTask = new Task(() =>
                    {
                        Thread.Sleep(5000);
                        Process tempProcess = new Process();
                        tempProcess.StartInfo.FileName = programPath;
                        tempProcess.StartInfo.WorkingDirectory = new FileInfo(programPath).Directory.ToString();
                        //tempProcess.StartInfo.UseShellExecute = false;
                        try { tempProcess.Start(); }
                        catch { }
                    });

                    processTask.Start();
                }
            }
        }

        private void SetupInitialHookEvents(int ind, DS4Device device)
        {
            ResetUdpSmoothingFilters(ind);

            // Set up filter for new input device
            OneEuroFilter tempFilter = new OneEuroFilter(OneEuroFilterPair.DEFAULT_WHEEL_CUTOFF,
                OneEuroFilterPair.DEFAULT_WHEEL_BETA);
            Mapping.wheelFilters[ind] = tempFilter;

            // Carry over initial profile wheel smoothing values to filter instances.
            // Set up event hooks to keep values in sync
            SteeringWheelSmoothingInfo wheelSmoothInfo = WheelSmoothInfo[ind];
            wheelSmoothInfo.SetFilterAttrs(tempFilter);
            wheelSmoothInfo.SetRefreshEvents(tempFilter);

            FlickStickSettings flickStickSettings = Global.LSOutputSettings[ind].outputSettings.flickSettings;
            flickStickSettings.RemoveRefreshEvents();
            flickStickSettings.SetRefreshEvents(Mapping.flickMappingData[ind].flickFilter);

            flickStickSettings = Global.RSOutputSettings[ind].outputSettings.flickSettings;
            flickStickSettings.RemoveRefreshEvents();
            flickStickSettings.SetRefreshEvents(Mapping.flickMappingData[ind].flickFilter);

            int tempIdx = ind;
            Global.L2OutputSettings[ind].ResetEvents();
            Global.L2ModInfo[ind].ResetEvents();
            Global.L2OutputSettings[ind].TriggerEffectChanged += (sender, e) =>
            {
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.L2ModInfo[ind].MaxOutputChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                L2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(Math.Max(tempInfo.maxOutput, tempInfo.maxZone) / 100.0 * 255.0);

                // Refresh trigger effect
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.L2ModInfo[ind].MaxZoneChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                L2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(Math.Max(tempInfo.maxOutput, tempInfo.maxZone) / 100.0 * 255.0);

                // Refresh trigger effect
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };

            Global.R2OutputSettings[ind].ResetEvents();
            Global.R2OutputSettings[ind].TriggerEffectChanged += (sender, e) =>
            {
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[tempIdx].TriggerEffect,
                    Global.R2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.R2ModInfo[ind].MaxOutputChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                R2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(tempInfo.maxOutput / 100.0 * 255.0);

                // Refresh trigger effect
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[tempIdx].TriggerEffect,
                    Global.R2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.R2ModInfo[ind].MaxZoneChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                R2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(tempInfo.maxOutput / 100.0 * 255.0);

                // Refresh trigger effect
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[tempIdx].TriggerEffect,
                    Global.R2OutputSettings[tempIdx].TrigEffectSettings);
            };
        }

        /// <summary>
        /// Perform Mapping property resetting as needed before loading profile settings
        /// </summary>
        /// <param name="device">Input device instance</param>
        public void PreLoadReset(int ind)
        {
            //DS4Device inputDevice = DS4Controllers[ind];
            //if (inputDevice == null)
            //{
            //    return;
            //}
            // Skip running for test profile with no mapping data
            if (ind >= Global.TEST_PROFILE_INDEX)
            {
                return;
            }

            // Reset current flick stick progress from previous profile
            Mapping.flickMappingData[ind].Reset();

            // Reset delta accel processors for sticks
            Mapping.deltaAccelProcessors[ind].LSProcessor.Reset();
            Mapping.deltaAccelProcessors[ind].RSProcessor.Reset();

            // Reset absolute mouse state data
            Mapping.absMouseOutputState[ind].Reset();

            // Reset some elements of current Mouse instance
            touchPad[ind]?.Reset();
        }

        public void TouchPadOn(int ind, DS4Device device)
        {
            Mouse tPad = touchPad[ind];
            //ITouchpadBehaviour tPad = touchPad[ind];
            device.Touchpad.TouchButtonDown += tPad.touchButtonDown;
            device.Touchpad.TouchButtonUp += tPad.touchButtonUp;
            device.Touchpad.TouchesBegan += tPad.touchesBegan;
            device.Touchpad.TouchesBegan += tPad.TouchStartedOrEnded;
            device.Touchpad.TouchesMoved += tPad.touchesMoved;
            device.Touchpad.TouchesEnded += tPad.touchesEnded;
            device.Touchpad.TouchesEnded += tPad.TouchStartedOrEnded;
            device.Touchpad.TouchUnchanged += tPad.touchUnchanged;
            //device.Touchpad.PreTouchProcess += delegate { touchPad[ind].populatePriorButtonStates(); };
            device.Touchpad.PreTouchProcess += (sender, args) => { touchPad[ind].populatePriorButtonStates(); };
            device.SixAxis.SixAccelMoved += tPad.sixaxisMoved;
            //LogDebug("Touchpad mode for " + device.MacAddress + " is now " + tmode.ToString());
            //Log.LogToTray("Touchpad mode for " + device.MacAddress + " is now " + tmode.ToString());
        }

        public string GetDS4Battery(int index)
        {
            DS4Device d = DS4Controllers[index];
            if (d != null)
            {
                string battery;
                if (!d.IsAlive())
                    battery = "...";

                if (d.isCharging())
                {
                    if (d.getBattery() >= 100)
                        battery = DS4WinWPF.Properties.Resources.Full;
                    else
                        battery = d.getBattery() + "%+";
                }
                else
                {
                    battery = d.getBattery() + "%";
                }

                return battery;
            }
            else
                return DS4WinWPF.Properties.Resources.NA;
        }

        protected void On_SerialChange(object sender, EventArgs e)
        {
            DS4Device device = (DS4Device)sender;
            int ind = -1;
            for (int i = 0, arlength = MAX_DS4_CONTROLLER_COUNT; ind == -1 && i < arlength; i++)
            {
                DS4Device tempDev = DS4Controllers[i];
                if (tempDev != null && device == tempDev)
                    ind = i;
            }

            if (ind >= 0)
            {
                OnDeviceSerialChange(this, ind, device.getMacAddress());
            }
        }

        protected void On_SyncChange(object sender, EventArgs e)
        {
            DS4Device device = (DS4Device)sender;
            int ind = -1;
            for (int i = 0, arlength = CURRENT_DS4_CONTROLLER_LIMIT; ind == -1 && i < arlength; i++)
            {
                DS4Device tempDev = DS4Controllers[i];
                if (tempDev != null && device == tempDev)
                    ind = i;
            }

            if (ind >= 0)
            {
                bool synced = device.isSynced();

                if (!synced)
                {
                    if (!useDInputOnly[ind])
                    {
                        Global.activeOutDevType[ind] = OutContType.None;
                        UnplugOutDev(ind, device);
                    }
                }
                else
                {
                    if (!getDInputOnly(ind))
                    {
                        touchPad[ind].ReplaceOneEuroFilterPair();
                        //touchPad[ind].ReplaceOneEuroFilterPair();

                        touchPad[ind].Cursor.ReplaceOneEuroFilterPair();
                        touchPad[ind].Cursor.SetupLateOneEuroFilters();
                        PluginOutDev(ind, device);
                    }
                }
            }
        }

        // Called when DS4 is disconnected or timed out
        protected void On_DS4Removal(object sender, EventArgs e)
        {
            DS4Device device = (DS4Device)sender;
            int ind = -1;
            for (int i = 0, arlength = DS4Controllers.Length; ind == -1 && i < arlength; i++)
            {
                if (DS4Controllers[i] != null && device.getMacAddress() == DS4Controllers[i].getMacAddress())
                    ind = i;
            }

            if (ind != -1)
            {
                bool removingStatus = false;
                lock (device.removeLocker)
                {
                    if (!device.IsRemoving)
                    {
                        removingStatus = true;
                        device.IsRemoving = true;
                    }
                }

                if (removingStatus)
                {
                    CurrentState[ind].Battery = PreviousState[ind].Battery = 0; // Reset for the next connection's initial status change.
                    if (!useDInputOnly[ind])
                    {
                        UnplugOutDev(ind, device);
                    }
                    else if (!device.PrimaryDevice)
                    {
                        OutputDevice outDev = outputDevices[ind];
                        if (outDev != null)
                        {
                            outDev.RemoveFeedback(ind);
                            outputDevices[ind] = null;
                        }
                    }

                    // Use Task to reset device synth state and commit it
                    Task.Run(() =>
                    {
                        Mapping.Commit(ind);
                    }).Wait();

                    string removed = DS4WinWPF.Properties.Resources.ControllerWasRemoved.Replace("*Mac address*", (ind + 1).ToString());
                    if (device.getBattery() <= 20 &&
                        device.getConnectionType() == ConnectionType.BT && !device.isCharging())
                    {
                        removed += ". " + DS4WinWPF.Properties.Resources.ChargeController;
                    }

                    LogDebug(removed);
                    AppLogger.LogToTray(removed);
                    dualSenseAudioPassthrough.Stop(ind);
                    dualSenseMicrophonePassthrough.Stop();
                    /*Stopwatch sw = new Stopwatch();
                    sw.Start();
                    while (sw.ElapsedMilliseconds < XINPUT_UNPLUG_SETTLE_TIME)
                    {
                        // Use SpinWait to keep control of current thread. Using Sleep could potentially
                        // cause other events to get run out of order
                        System.Threading.Thread.SpinWait(500);
                    }
                    sw.Stop();
                    */

                    device.IsRemoved = true;
                    device.Synced = false;
                    DS4Controllers[ind] = null;
                    oscState[ind] = new DS4State();
                    //eventDispatcher.Invoke(() =>
                    //{
                    slotManager.RemoveController(device, ind);
                    if (isUsingOSCSender())
                    {
                        oscSender.Send(new SharpOSC.OscMessage("/ds4windows/monitor/" + ind + "/plug", 0));
                    }
                    //});

                    touchPad[ind] = null;
                    lag[ind] = false;
                    inWarnMonitor[ind] = false;
                    useDInputOnly[ind] = true;
                    Global.activeOutDevType[ind] = OutContType.None;
                    /* Leave up to Auto Profile system to change the following flags? */
                    //Global.useTempProfile[ind] = false;
                    //Global.tempprofilename[ind] = string.Empty;
                    //Global.tempprofileDistance[ind] = false;

                    //Thread.Sleep(XINPUT_UNPLUG_SETTLE_TIME);
                }
            }
        }

        public bool[] lag = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        public bool[] inWarnMonitor = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private byte[] currentBattery = new byte[MAX_DS4_CONTROLLER_COUNT] { 0, 0, 0, 0, 0, 0, 0, 0 };
        private bool[] charging = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] tempStrings = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private bool[] gameBarProfileActive = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] gameBarProfilePending = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private DateTime[] gameBarProfileActivatedUtc = new DateTime[MAX_DS4_CONTROLLER_COUNT];
        private DateTime[] gameBarProfileRequestedUtc = new DateTime[MAX_DS4_CONTROLLER_COUNT];
        private string[] gameBarRequestedProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private bool[] gameBarPreviousUseTempProfile = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] gameBarPreviousTempProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private string[] gameBarPreviousProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private DateTime[] gameBarHomeButtonIgnoreUntilUtc = new DateTime[MAX_DS4_CONTROLLER_COUNT];
        private DateTime gameBarLastVisibleUtc = DateTime.MinValue;
        private DateTime gameBarInvisibleSinceUtc = DateTime.MinValue;
        private DateTime gameBarLastVisibilityCheckUtc = DateTime.MinValue;
        private bool gameBarVerboseDetectionLogInitialized = false;
        private bool gameBarVerboseLastVisible = false;
        private DateTime gameBarVerboseLastDetectionLogUtc = DateTime.MinValue;
        private bool[] dualSenseMuteButtonWasDown = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteLedOn = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteLedOverrideActive = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteProfilePending = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] dualSenseMuteRequestedProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private bool[] dualSenseMuteRequestedProfileRestore = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteRequestedRestoreWasTemporary = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteReturnProfileActive = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteReturnProfileWasTemporary = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] dualSenseMuteReturnProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };

        public bool IsGameBarProfilePriorityActive(int ind)
        {
            return ind >= 0 && ind < MAX_DS4_CONTROLLER_COUNT &&
                (gameBarProfileActive[ind] || gameBarProfilePending[ind]);
        }

        public bool IsAnyGameBarProfilePriorityActive()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (IsGameBarProfilePriorityActive(i))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryDeferAutoProfileForGameBar(int ind, string profileName)
        {
            if (!IsGameBarProfilePriorityActive(ind))
            {
                return false;
            }

            gameBarPreviousUseTempProfile[ind] = true;
            gameBarPreviousTempProfileName[ind] = profileName;
            if (!string.IsNullOrEmpty(Global.ProfilePath[ind]))
            {
                gameBarPreviousProfileName[ind] = Global.ProfilePath[ind];
            }

            return true;
        }

        public bool TryDeferAutoProfileDefaultForGameBar(int ind)
        {
            if (!IsGameBarProfilePriorityActive(ind))
            {
                return false;
            }

            gameBarPreviousUseTempProfile[ind] = false;
            gameBarPreviousTempProfileName[ind] = string.Empty;
            if (!string.IsNullOrEmpty(Global.ProfilePath[ind]))
            {
                gameBarPreviousProfileName[ind] = Global.ProfilePath[ind];
            }

            return true;
        }

        private bool HasAnyConfiguredGameBarProfile()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (DS4Controllers[i] != null &&
                    Global.GameBarHomeButtonSupport[i] &&
                    !string.IsNullOrEmpty(Global.GameBarProfileName[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetConfiguredGameBarProfileName(int ind, out string profileName)
        {
            profileName = string.Empty;
            if (!Global.GameBarHomeButtonSupport[ind])
            {
                return false;
            }

            profileName = Global.GameBarProfileName[ind];
            if (string.IsNullOrEmpty(profileName))
            {
                return false;
            }

            string profilePath = Path.Combine(appdatapath, "Profiles", $"{profileName}.xml");
            if (!File.Exists(profilePath))
            {
                return false;
            }

            return true;
        }

        private void RequestGameBarProfilePriority(int ind, string profileName, DateTime now)
        {
            if (IsGameBarProfilePriorityActive(ind))
            {
                return;
            }

            gameBarPreviousUseTempProfile[ind] = Global.useTempProfile[ind];
            gameBarPreviousTempProfileName[ind] = Global.tempprofilename[ind];
            gameBarPreviousProfileName[ind] = Global.ProfilePath[ind];
            gameBarRequestedProfileName[ind] = profileName;
            gameBarProfileRequestedUtc[ind] = now;
            gameBarProfilePending[ind] = true;
            StartupDiag($"GameBar profile requested controller={ind + 1} target='{profileName}' previousTemp={gameBarPreviousUseTempProfile[ind]} previousTempProfile='{gameBarPreviousTempProfileName[ind]}' previousProfile='{gameBarPreviousProfileName[ind]}'");
        }

        private void CheckGameBarHomeButton(int ind, DS4State cState, DS4State tempControlState, DS4State pState)
        {
            if (!cState.PS)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < gameBarHomeButtonIgnoreUntilUtc[ind])
            {
                cState.PS = false;
                tempControlState.PS = false;
                return;
            }

            if (pState.PS)
            {
                return;
            }

            if (gameBarProfileActive[ind] || gameBarProfilePending[ind])
            {
                cState.PS = false;
                tempControlState.PS = false;
                string openResult = gameBarIntegration.OpenGameBar();
                StartupDiag($"GameBar home button controller={ind + 1} existingPriority active={gameBarProfileActive[ind]} pending={gameBarProfilePending[ind]} {openResult}");
                gameBarHomeButtonIgnoreUntilUtc[ind] = now + TimeSpan.FromSeconds(1);
                return;
            }

            if (!TryGetConfiguredGameBarProfileName(ind, out string profileName))
            {
                return;
            }

            cState.PS = false;
            tempControlState.PS = false;
            gameBarHomeButtonIgnoreUntilUtc[ind] = now + TimeSpan.FromSeconds(1);
            RequestGameBarProfilePriority(ind, profileName, now);
            string result = gameBarIntegration.OpenGameBar();
            StartupDiag($"GameBar home button controller={ind + 1} requestedProfile='{profileName}' {result}");
        }

        public void UpdateGameBarProfileState()
        {
            if (!running)
            {
                return;
            }

            if (Interlocked.Exchange(ref gameBarProfileUpdateGate, 1) == 1)
            {
                return;
            }

            try
            {
                bool anyActiveOrPending = IsAnyGameBarProfilePriorityActive();
                bool anyConfigured = HasAnyConfiguredGameBarProfile();
                bool anyMutePending = HasAnyPendingDualSenseMuteProfile();

                if (!anyActiveOrPending && !anyConfigured && !anyMutePending)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (now - gameBarLastVisibilityCheckUtc < TimeSpan.FromMilliseconds(350))
                {
                    return;
                }

                gameBarLastVisibilityCheckUtc = now;
                bool gameBarVisible = gameBarIntegration.IsGameBarVisible();
                LogGameBarDetectionIfVerbose(now, gameBarVisible, anyConfigured, anyActiveOrPending);
                if (gameBarVisible)
                {
                    gameBarLastVisibleUtc = now;
                    gameBarInvisibleSinceUtc = DateTime.MinValue;
                    RequestVisibleGameBarProfiles(now);
                    ActivatePendingGameBarProfiles(now);
                    return;
                }

                ProcessPendingDualSenseMuteProfiles();

                if (gameBarInvisibleSinceUtc == DateTime.MinValue)
                {
                    gameBarInvisibleSinceUtc = now;
                }

                for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
                {
                    if (gameBarProfilePending[i] && now - gameBarProfileRequestedUtc[i] > TimeSpan.FromSeconds(6))
                    {
                        ClearPendingGameBarProfile(i);
                    }

                    if (!gameBarProfileActive[i])
                    {
                        continue;
                    }

                    bool activationGraceElapsed = now - gameBarProfileActivatedUtc[i] > TimeSpan.FromSeconds(3);
                    bool visibilityGraceElapsed = gameBarLastVisibleUtc == DateTime.MinValue ||
                        now - gameBarLastVisibleUtc > TimeSpan.FromMilliseconds(1500);
                    bool invisibleStable = gameBarInvisibleSinceUtc != DateTime.MinValue &&
                        now - gameBarInvisibleSinceUtc > TimeSpan.FromMilliseconds(1500);

                    if (activationGraceElapsed && visibilityGraceElapsed && invisibleStable &&
                        RestorePreviousGameBarProfile(i))
                    {
                        gameBarProfileActive[i] = false;
                        gameBarPreviousUseTempProfile[i] = false;
                        gameBarPreviousTempProfileName[i] = string.Empty;
                        gameBarPreviousProfileName[i] = string.Empty;
                        gameBarRequestedProfileName[i] = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                StartupDiag($"UpdateGameBarProfileState exception {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref gameBarProfileUpdateGate, 0);
            }
        }

        private void LogGameBarDetectionIfVerbose(DateTime now, bool gameBarVisible, bool anyConfigured, bool anyActiveOrPending)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            bool shouldLog = !gameBarVerboseDetectionLogInitialized ||
                gameBarVisible != gameBarVerboseLastVisible ||
                now - gameBarVerboseLastDetectionLogUtc > TimeSpan.FromSeconds(3);

            if (!shouldLog)
            {
                return;
            }

            gameBarVerboseDetectionLogInitialized = true;
            gameBarVerboseLastVisible = gameBarVisible;
            gameBarVerboseLastDetectionLogUtc = now;
            StartupDiag($"GameBar detection visible={gameBarVisible} anyConfigured={anyConfigured} anyActiveOrPending={anyActiveOrPending} {gameBarIntegration.LastDetectionSummary} controllers={BuildGameBarPriorityStateSummary()}");
        }

        private string BuildGameBarPriorityStateSummary()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (DS4Controllers[i] == null &&
                    !gameBarProfileActive[i] &&
                    !gameBarProfilePending[i])
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" ");
                }

                builder.Append("C");
                builder.Append(i + 1);
                builder.Append("[connected=");
                builder.Append(DS4Controllers[i] != null);
                builder.Append(",enabled=");
                builder.Append(Global.GameBarHomeButtonSupport[i]);
                builder.Append(",target='");
                builder.Append(Global.GameBarProfileName[i]);
                builder.Append("',active=");
                builder.Append(gameBarProfileActive[i]);
                builder.Append(",pending=");
                builder.Append(gameBarProfilePending[i]);
                builder.Append(",requested='");
                builder.Append(gameBarRequestedProfileName[i]);
                builder.Append("']");
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private void RequestVisibleGameBarProfiles(DateTime now)
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (DS4Controllers[i] == null || IsGameBarProfilePriorityActive(i))
                {
                    continue;
                }

                if (TryGetConfiguredGameBarProfileName(i, out string profileName))
                {
                    RequestGameBarProfilePriority(i, profileName, now);
                }
            }
        }

        private void ActivatePendingGameBarProfiles(DateTime now)
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (!gameBarProfilePending[i])
                {
                    continue;
                }

                string profileName = gameBarRequestedProfileName[i];
                bool actionRan = true;
                bool profileLoaded = false;
                if (!string.IsNullOrEmpty(profileName))
                {
                    actionRan = RunProfileActionWithReportingPaused(i,
                        () => profileLoaded = Global.LoadTempProfile(i, profileName, false, this));
                }

                if (!actionRan)
                {
                    StartupDiag($"GameBar profile activation skipped controller={i + 1} target='{profileName}' reason=reporting-action-not-run");
                    continue;
                }

                if (profileLoaded)
                {
                    gameBarProfileActive[i] = true;
                    gameBarProfileActivatedUtc[i] = now;
                    StartupDiag($"GameBar profile activated controller={i + 1} target='{profileName}'");
                }
                else
                {
                    StartupDiag($"GameBar profile activation failed controller={i + 1} target='{profileName}'");
                }

                ClearPendingGameBarProfile(i);
            }
        }

        private void ClearPendingGameBarProfile(int ind)
        {
            gameBarProfilePending[ind] = false;
            gameBarProfileRequestedUtc[ind] = DateTime.MinValue;

            if (!gameBarProfileActive[ind])
            {
                gameBarRequestedProfileName[ind] = string.Empty;
                gameBarPreviousUseTempProfile[ind] = false;
                gameBarPreviousTempProfileName[ind] = string.Empty;
                gameBarPreviousProfileName[ind] = string.Empty;
            }
        }

        private bool RestorePreviousGameBarProfile(int ind)
        {
            bool actionRan = true;
            bool restored = false;

            if (gameBarPreviousUseTempProfile[ind] &&
                !string.IsNullOrEmpty(gameBarPreviousTempProfileName[ind]))
            {
                actionRan = RunProfileActionWithReportingPaused(ind,
                    () => restored = Global.LoadTempProfile(ind, gameBarPreviousTempProfileName[ind], false, this));
                if (!actionRan)
                {
                    StartupDiag($"GameBar profile restore skipped controller={ind + 1} targetTemp='{gameBarPreviousTempProfileName[ind]}' reason=reporting-action-not-run");
                    return false;
                }

                if (restored)
                {
                    StartupDiag($"GameBar profile restored controller={ind + 1} tempProfile='{gameBarPreviousTempProfileName[ind]}'");
                    return true;
                }
            }

            string previousProfileName = gameBarPreviousProfileName[ind];
            if (!string.IsNullOrEmpty(previousProfileName))
            {
                Global.ProfilePath[ind] = previousProfileName;
                Global.OlderProfilePath[ind] = previousProfileName;
            }

            actionRan = RunProfileActionWithReportingPaused(ind,
                () => restored = Global.LoadProfile(ind, false, this));
            StartupDiag($"GameBar profile restore controller={ind + 1} profile='{previousProfileName}' actionRan={actionRan} restored={restored}");
            return actionRan && restored;
        }

        private bool RunProfileActionWithReportingPaused(int ind, Action action)
        {
            DS4Device device = ind >= 0 && ind < DS4Controllers.Length ? DS4Controllers[ind] : null;
            if (device == null)
            {
                action?.Invoke();
                return true;
            }

            bool actionRan = false;
            device.HaltReportingRunAction(() =>
            {
                actionRan = true;
                action?.Invoke();
            });

            return actionRan;
        }

        private bool HasAnyPendingDualSenseMuteProfile()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (dualSenseMuteProfilePending[i])
                {
                    return true;
                }
            }

            return false;
        }

        private bool QueueDualSenseMuteProfile(int ind, string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                return false;
            }

            string profilePath = Path.Combine(appdatapath, "Profiles", $"{profileName}.xml");
            if (!File.Exists(profilePath))
            {
                LogDebug($"DualSense mute profile action skipped. Profile '{profileName}' was not found.", true);
                return false;
            }

            dualSenseMuteRequestedProfileName[ind] = profileName;
            dualSenseMuteRequestedProfileRestore[ind] = false;
            dualSenseMuteRequestedRestoreWasTemporary[ind] = false;
            dualSenseMuteProfilePending[ind] = true;
            return true;
        }

        // WM_DEVICECHANGE is advisory: USB devices can disappear while their pending HID read
        // remains blocked. Reconcile the tracked slots before scanning for arrivals so a dead
        // path cannot keep its virtual output or prevent the same controller from reconnecting.
        private void ReconcileDisconnectedControllers()
        {
            for (int index = 0; index < DS4Controllers.Length; index++)
            {
                DS4Device device = DS4Controllers[index];
                if (device == null || device.IsRemoving)
                {
                    continue;
                }

                bool isConnected;
                try
                {
                    isConnected = device.HidDevice.IsConnected;
                }
                catch
                {
                    // A failed presence probe must never evict an otherwise working controller.
                    continue;
                }

                if (isConnected)
                {
                    continue;
                }

                LogDebug($"Controller {index + 1} HID path disappeared; reconciling removal for {device.getMacAddress()} ({device.getConnectionType()}).");
                device.StopUpdate();
                On_DS4Removal(device, EventArgs.Empty);
                DS4Devices.RemoveDevice(device);
            }
        }

        private bool QueueDualSenseMuteProfileRestore(int ind, string profileName, bool wasTemporary)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                return false;
            }

            string profilePath = Path.Combine(appdatapath, "Profiles", $"{profileName}.xml");
            if (!File.Exists(profilePath))
            {
                LogDebug($"DualSense mute profile return skipped. Profile '{profileName}' was not found.", true);
                return false;
            }

            dualSenseMuteRequestedProfileName[ind] = profileName;
            dualSenseMuteRequestedProfileRestore[ind] = true;
            dualSenseMuteRequestedRestoreWasTemporary[ind] = wasTemporary;
            dualSenseMuteProfilePending[ind] = true;
            return true;
        }

        private void ProcessPendingDualSenseMuteProfiles()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (!dualSenseMuteProfilePending[i])
                {
                    continue;
                }

                string profileName = dualSenseMuteRequestedProfileName[i];
                bool restoreRequested = dualSenseMuteRequestedProfileRestore[i];
                bool restoreWasTemporary = dualSenseMuteRequestedRestoreWasTemporary[i];
                bool profileLoaded = false;
                bool actionRan = RunProfileActionWithReportingPaused(i, () =>
                {
                    if (restoreRequested && !restoreWasTemporary)
                    {
                        Global.ProfilePath[i] = profileName;
                        Global.OlderProfilePath[i] = profileName;
                        profileLoaded = Global.LoadProfile(i, false, this);
                    }
                    else
                    {
                        profileLoaded = Global.LoadTempProfile(i, profileName, false, this);
                    }
                });

                if (!actionRan)
                {
                    continue;
                }

                dualSenseMuteProfilePending[i] = false;
                dualSenseMuteRequestedProfileName[i] = string.Empty;
                dualSenseMuteRequestedProfileRestore[i] = false;
                dualSenseMuteRequestedRestoreWasTemporary[i] = false;

                if (!profileLoaded)
                {
                    LogDebug($"DualSense mute profile action failed to load '{profileName}'.", true);
                }
            }
        }

        private void CheckDualSenseMuteButtonProfileActions(int ind, DS4State cState)
        {
            if (!(DS4Controllers[ind] is InputDevices.DualSenseDevice dualSenseDevice))
            {
                dualSenseMuteButtonWasDown[ind] = false;
                dualSenseMuteLedOverrideActive[ind] = false;
                dualSenseMuteReturnProfileActive[ind] = false;
                dualSenseMuteReturnProfileWasTemporary[ind] = false;
                dualSenseMuteReturnProfileName[ind] = string.Empty;
                return;
            }

            bool muteLightEnabled = Global.DualSenseMuteButtonLightEnabled[ind];
            bool hasProfileAction = !string.IsNullOrEmpty(Global.DualSenseMuteOnProfileName[ind]) ||
                !string.IsNullOrEmpty(Global.DualSenseMuteOffProfileName[ind]) ||
                Global.DualSenseMuteDefaultBackToThisProfile[ind];
            if (!muteLightEnabled && !hasProfileAction)
            {
                if (dualSenseMuteLedOverrideActive[ind])
                {
                    dualSenseDevice.SetProfileMuteLedState(false, false);
                    dualSenseMuteLedOverrideActive[ind] = false;
                }

                dualSenseMuteButtonWasDown[ind] = cState.Mute;
                return;
            }

            bool muteDown = cState.Mute;
            if (muteDown && !dualSenseMuteButtonWasDown[ind])
            {
                dualSenseMuteLedOn[ind] = !dualSenseMuteLedOn[ind];
                if (muteLightEnabled)
                {
                    dualSenseDevice.SetProfileMuteLedState(true, dualSenseMuteLedOn[ind]);
                    dualSenseMuteLedOverrideActive[ind] = true;
                }
                else if (dualSenseMuteLedOverrideActive[ind])
                {
                    dualSenseDevice.SetProfileMuteLedState(false, false);
                    dualSenseMuteLedOverrideActive[ind] = false;
                }

                if (dualSenseMuteLedOn[ind])
                {
                    string requestedProfileName = Global.DualSenseMuteOnProfileName[ind];
                    bool shouldReturnToCurrentProfile =
                        Global.DualSenseMuteDefaultBackToThisProfile[ind] &&
                        !string.IsNullOrEmpty(requestedProfileName);
                    string currentProfileName = string.Empty;
                    bool currentProfileWasTemporary = false;
                    if (shouldReturnToCurrentProfile)
                    {
                        currentProfileName = Global.useTempProfile[ind] ?
                            Global.tempprofilename[ind] : Global.ProfilePath[ind];
                        currentProfileWasTemporary = Global.useTempProfile[ind];
                    }

                    if (QueueDualSenseMuteProfile(ind, requestedProfileName))
                    {
                        dualSenseMuteReturnProfileActive[ind] = shouldReturnToCurrentProfile &&
                            !string.IsNullOrEmpty(currentProfileName);
                        dualSenseMuteReturnProfileWasTemporary[ind] = currentProfileWasTemporary;
                        dualSenseMuteReturnProfileName[ind] = currentProfileName;
                    }
                }
                else
                {
                    if (dualSenseMuteReturnProfileActive[ind])
                    {
                        bool returnQueued = QueueDualSenseMuteProfileRestore(ind,
                            dualSenseMuteReturnProfileName[ind],
                            dualSenseMuteReturnProfileWasTemporary[ind]);
                        if (returnQueued)
                        {
                            dualSenseMuteReturnProfileActive[ind] = false;
                            dualSenseMuteReturnProfileWasTemporary[ind] = false;
                            dualSenseMuteReturnProfileName[ind] = string.Empty;
                        }
                    }
                    else
                    {
                        // The currently active profile owns the next step. This
                        // makes A -> B -> C chains deterministic and avoids
                        // leaking A's Mute Off target into B.
                        QueueDualSenseMuteProfile(ind, Global.DualSenseMuteOffProfileName[ind]);
                    }
                }
            }
            else if (muteLightEnabled && !dualSenseMuteLedOverrideActive[ind])
            {
                dualSenseDevice.SetProfileMuteLedState(true, dualSenseMuteLedOn[ind]);
                dualSenseMuteLedOverrideActive[ind] = true;
            }

            dualSenseMuteButtonWasDown[ind] = muteDown;
        }

        private void StartGameBarProfileTimer()
        {
            if (gameBarProfileTimer != null)
            {
                return;
            }

            gameBarProfileTimer = new System.Threading.Timer(_ => UpdateGameBarProfileState(),
                null, TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(350));
        }

        private void StopGameBarProfileTimer()
        {
            System.Threading.Timer timer = Interlocked.Exchange(ref gameBarProfileTimer, null);
            timer?.Dispose();
        }

        // Called every time a new input report has arrived
        protected void On_Report(DS4Device device, EventArgs e, int ind)
        {
            if (ind != -1)
            {
                int startupReportCount = 0;
                bool startupReportDiag = false;
                if (Global.VerboseStartupLogging)
                {
                    startupReportCount = ++startupReportDiagCounts[ind];
                    startupReportDiag = startupReportCount <= 5 || startupReportCount == 50;
                    if (startupReportDiag)
                    {
                        StartupDiag($"On_Report enter index={ind} count={startupReportCount} synced={device.isSynced()} latency={device.Latency} useDInputOnly={useDInputOnly[ind]} activeOut={activeOutDevType[ind]} outDev={outputDevices[ind]?.GetDeviceType() ?? "null"}");
                    }
                }

                string devError = tempStrings[ind] = device.error;
                if (!string.IsNullOrEmpty(devError))
                {
                    LogDebug(devError);
                }

                if (inWarnMonitor[ind])
                {
                    int flashWhenLateAt = getFlashWhenLateAt();
                    if (!lag[ind] && device.Latency >= flashWhenLateAt)
                    {
                        lag[ind] = true;
                        LagFlashWarning(device, ind, true);
                    }
                    else if (lag[ind] && device.Latency < flashWhenLateAt)
                    {
                        lag[ind] = false;
                        LagFlashWarning(device, ind, false);
                    }
                }
                else
                {
                    if (DateTime.UtcNow - device.firstActive > TimeSpan.FromSeconds(5))
                    {
                        inWarnMonitor[ind] = true;
                    }
                }

                DS4State cState, tempControlState;
                if (!device.PerformStateMerge)
                {
                    cState = CurrentState[ind];
                    device.getRawCurrentState(cState);
                    tempControlState = CurrentState[ind];
                }
                else
                {
                    cState = device.JointState;
                    device.MergeStateData(cState);
                    // Need to copy state object info for use in UDP server
                    cState.CopyTo(CurrentState[ind]);
                    tempControlState = CurrentState[ind];
                }

                LogViiperInputLayerDiag(ind, "raw", cState);

                DS4State pState = device.getPreviousStateRef();
                //device.getPreviousState(PreviousState[ind]);
                //DS4State pState = PreviousState[ind];

                if (device.firstReport && device.isSynced())
                {
                    // Only send Log message when device is considered a primary device
                    if (device.PrimaryDevice)
                    {
                        if (File.Exists(Path.Combine(appdatapath, "Profiles", $"{ProfilePath[ind]}.xml")))
                        {
                            string prolog = string.Format(DS4WinWPF.Properties.Resources.UsingProfile, (ind + 1).ToString(), ProfilePath[ind], $"{device.Battery}");
                            LogDebug(prolog);
                            AppLogger.LogToTray(prolog);
                        }
                        else
                        {
                            string prolog = string.Format(DS4WinWPF.Properties.Resources.NotUsingProfile, (ind + 1).ToString(), $"{device.Battery}");
                            LogDebug(prolog);
                            AppLogger.LogToTray(prolog);
                        }
                    }

                    device.firstReport = false;
                }

                if (device.PrimaryDevice && Global.UseIconChoice == TrayIconChoice.Battery)
                {
                    InvokeBatteryChanged(cState.Battery);
                }

                if (!device.PrimaryDevice)
                {
                    // Make sure a joined device is still linked
                    int jointInd = device.JointDeviceSlotNumber;
                    if (device.OutputMapGyro &&
                        jointInd != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                    {
                        // Output changes from Gyro data early. Seems better to ME... REE
                        GyroOutMode imuOutMode = Global.GetGyroOutMode(device.JointDeviceSlotNumber);
                        if (imuOutMode != GyroOutMode.None)
                        {
                            if (imuOutMode == GyroOutMode.Mouse)
                            {
                                outputKBMHandler.Sync();
                            }
                            else if (imuOutMode == GyroOutMode.MouseJoystick)
                            {
                                // Add new Mapping method and add data to
                                // parent device state
                                DS4State tempMapState = MappedState[jointInd];
                                Mapping.TempMouseJoystick(jointInd, tempMapState);
                                if (!useDInputOnly[jointInd])
                                {
                                    outputDevices[jointInd]?.ConvertandSendReport(tempMapState, jointInd);
                                }
                            }
                        }
                    }
                    else if (!device.OutputMapGyro)
                    {
                        // Copy for use in UDP
                        tempControlState.Motion = device.GetRawCurrentStateRef().Motion;
                    }

                    // Skip mapping routine if part of a joined device
                    return;
                }

                CheckGameBarHomeButton(ind, cState, tempControlState, pState);
                CheckDualSenseMuteButtonProfileActions(ind, cState);

                if (getEnableTouchToggle(ind))
                {
                    CheckForTouchToggle(ind, cState, pState);
                }

                cState = device.Debouncer.ProcessInput(cState);
                LogViiperInputLayerDiag(ind, "debounced", cState);

                if (startupReportDiag)
                {
                    StartupDiag($"On_Report pre-map index={ind} count={startupReportCount} buttons Cross={cState.Cross} Circle={cState.Circle} PS={cState.PS} LX={cState.LX} LY={cState.LY} RX={cState.RX} RY={cState.RY} L2={cState.L2} R2={cState.R2}");
                }

                cState = Mapping.SetCurveAndDeadzone(ind, cState, TempState[ind]);
                LogViiperInputLayerDiag(ind, "curved", cState);

                if (!recordingMacro && (useTempProfile[ind] ||
                    containsCustomAction(ind) || containsCustomExtras(ind) ||
                    getProfileActionCount(ind) > 0))
                {
                    DS4State tempMapState = MappedState[ind];
                    DS4State oscMapState = oscState[ind];

                    if (isUsingOSCSender())
                    {
                        OSCPreMappingStep(ind, cState, tempMapState, oscMapState);
                    }

                    if (startupReportDiag)
                    {
                        StartupDiag($"On_Report MapCustom begin index={ind} count={startupReportCount}");
                    }
                    Mapping.MapCustom(ind, cState, tempMapState, ExposedState[ind], touchPad[ind], this);
                    if (startupReportDiag)
                    {
                        StartupDiag($"On_Report MapCustom end index={ind} count={startupReportCount}");
                    }

                    // Copy current Touchpad and Gyro data
                    // Might change to use new DS4State.CopyExtrasTo method
                    tempMapState.Motion = cState.Motion;
                    tempMapState.ds4Timestamp = cState.ds4Timestamp;
                    tempMapState.FrameCounter = cState.FrameCounter;
                    tempMapState.TouchPacketCounter = cState.TouchPacketCounter;
                    tempMapState.TrackPadTouch0 = cState.TrackPadTouch0;
                    tempMapState.TrackPadTouch1 = cState.TrackPadTouch1;

                    if (isUsingOSCServer())
                    {
                        OSCPostMappingStep(tempMapState, oscMapState);
                    }

                    cState = tempMapState;
                    LogViiperInputLayerDiag(ind, "mapped", cState);

                }

                if (!useDInputOnly[ind])
                {
                    // Perform this virtual trigger button check in post
                    if (activeOutDevType[ind] == OutContType.DS4)
                    {
                        DS4TriggerOutputMode trigMode = Global.GetOutputDS4TriggerMode(ind);
                        if (trigMode == DS4TriggerOutputMode.Default)
                        {
                            cState.L2Btn = cState.L2 > 0;
                            cState.R2Btn = cState.R2 > 0;
                        }
                        else if (trigMode == DS4TriggerOutputMode.Buttons)
                        {
                            cState.L2Btn = cState.L2 > 0;
                            cState.R2Btn = cState.R2 > 0;
                            // Disable analog output
                            cState.L2 = 0;
                            cState.R2 = 0;
                        }
                    }

                    if (startupReportDiag)
                    {
                        StartupDiag($"On_Report ConvertandSendReport begin index={ind} count={startupReportCount} outDev={outputDevices[ind]?.GetDeviceType() ?? "null"}");
                    }
                    LogViiperInputLayerDiag(ind, "output", cState);
                    outputDevices[ind]?.ConvertandSendReport(cState, ind);
                    if (startupReportDiag)
                    {
                        StartupDiag($"On_Report ConvertandSendReport end index={ind} count={startupReportCount}");
                    }
                    //testNewReport(ref x360reports[ind], cState, ind);
                    //x360controls[ind]?.SendReport(x360reports[ind]);

                    //x360Bus.Parse(cState, processingData[ind].Report, ind);
                    // We push the translated Xinput state, and simultaneously we
                    // pull back any possible rumble data coming from Xinput consumers.
                    /*if (x360Bus.Report(processingData[ind].Report, processingData[ind].Rumble))
                    {
                        byte Big = processingData[ind].Rumble[3];
                        byte Small = processingData[ind].Rumble[4];

                        if (processingData[ind].Rumble[1] == 0x08)
                        {
                            SetDevRumble(device, Big, Small, ind);
                        }
                    }
                    */
                }
                else
                {
                    // UseDInputOnly profile may re-map sixaxis gyro sensor values as a VJoy joystick axis (steering wheel emulation mode using VJoy output device). Handle this option because VJoy output works even in USeDInputOnly mode.
                    // If steering wheel emulation uses LS/RS/R2/L2 output axies then the profile should NOT use UseDInputOnly option at all because those require a virtual output device.
                    SASteeringWheelEmulationAxisType steeringWheelMappedAxis = Global.GetSASteeringWheelEmulationAxis(ind);
                    switch (steeringWheelMappedAxis)
                    {
                        case SASteeringWheelEmulationAxisType.None: break;

                        case SASteeringWheelEmulationAxisType.VJoy1X:
                        case SASteeringWheelEmulationAxisType.VJoy2X:
                            VJoyFeeder.vJoyFeeder.FeedAxisValue(cState.SASteeringWheelEmulationUnit, ((((uint)steeringWheelMappedAxis) - ((uint)SASteeringWheelEmulationAxisType.VJoy1X)) / 3) + 1, VJoyFeeder.HID_USAGES.HID_USAGE_X);
                            break;

                        case SASteeringWheelEmulationAxisType.VJoy1Y:
                        case SASteeringWheelEmulationAxisType.VJoy2Y:
                            VJoyFeeder.vJoyFeeder.FeedAxisValue(cState.SASteeringWheelEmulationUnit, ((((uint)steeringWheelMappedAxis) - ((uint)SASteeringWheelEmulationAxisType.VJoy1X)) / 3) + 1, VJoyFeeder.HID_USAGES.HID_USAGE_Y);
                            break;

                        case SASteeringWheelEmulationAxisType.VJoy1Z:
                        case SASteeringWheelEmulationAxisType.VJoy2Z:
                            VJoyFeeder.vJoyFeeder.FeedAxisValue(cState.SASteeringWheelEmulationUnit, ((((uint)steeringWheelMappedAxis) - ((uint)SASteeringWheelEmulationAxisType.VJoy1X)) / 3) + 1, VJoyFeeder.HID_USAGES.HID_USAGE_Z);
                            break;

                        default: break;
                    }
                }

                // Output any synthetic events.
                if (startupReportDiag)
                {
                    StartupDiag($"On_Report Mapping.Commit begin index={ind} count={startupReportCount}");
                }
                Mapping.Commit(ind);
                if (startupReportDiag)
                {
                    StartupDiag($"On_Report Mapping.Commit end index={ind} count={startupReportCount}");
                }

                // Update the Lightbar color
                if (startupReportDiag)
                {
                    StartupDiag($"On_Report updateLightBar begin index={ind} count={startupReportCount}");
                }
                DS4LightBar.updateLightBar(device, ind);
                if (startupReportDiag)
                {
                    StartupDiag($"On_Report updateLightBar end index={ind} count={startupReportCount}");
                }

                if (device.PerformStateMerge)
                {
                    device.PreserveMergedStateData();
                }

                if (device.PerformStateMerge && !device.OutputMapGyro)
                {
                    // Copy for use in UDP
                    tempControlState.Motion = device.GetRawCurrentStateRef().Motion;
                }

                if (startupReportDiag)
                {
                    StartupDiag($"On_Report exit index={ind} count={startupReportCount}");
                }
            }
        }

        private void LogViiperInputLayerDiag(int ind, string stage, DS4State state)
        {
            if (!Global.VerboseStartupLogging ||
                ind < 0 ||
                ind >= MAX_DS4_CONTROLLER_COUNT ||
                state == null ||
                !ViiperOutDevice.IsViiperType(activeOutDevType[ind]) ||
                !IsInputStateNonNeutralForDiag(state))
            {
                return;
            }

            int count = Interlocked.Increment(ref viiperInputLayerDiagCounts[ind]);
            if (count > 800)
            {
                return;
            }

            LogDebug(
                $"VIIPER_LAYER_DIAG stage={stage} device={ind + 1} count={count} profile=\"{ProfilePath[ind]}\" out={activeOutDevType[ind]} {SummarizeInputStateForDiag(state)}",
                false);
        }

        private static bool IsInputStateNonNeutralForDiag(DS4State state)
        {
            return state.LX != 128 || state.LY != 128 ||
                state.RX != 128 || state.RY != 128 ||
                state.L2 != 0 || state.R2 != 0 ||
                state.L2Btn || state.R2Btn ||
                state.DpadUp || state.DpadDown || state.DpadLeft || state.DpadRight ||
                state.Cross || state.Circle || state.Square || state.Triangle ||
                state.L1 || state.R1 || state.L3 || state.R3 ||
                state.Share || state.Options || state.PS || state.Mute ||
                state.TouchButton || state.OutputTouchButton ||
                state.Touch1 || state.Touch2 ||
                state.Touch1Finger || state.Touch2Fingers ||
                state.TrackPadTouch0.IsActive || state.TrackPadTouch1.IsActive ||
                state.FnL || state.FnR || state.BLP || state.BRP ||
                state.Capture || state.SideL || state.SideR;
        }

        private static string SummarizeInputStateForDiag(DS4State state)
        {
            return $"packet={state.PacketCounter} frame={state.FrameCounter} lx={state.LX} ly={state.LY} rx={state.RX} ry={state.RY} " +
                $"l2={state.L2}/{state.L2Btn} r2={state.R2}/{state.R2Btn} " +
                $"dpad=U{state.DpadUp}D{state.DpadDown}L{state.DpadLeft}R{state.DpadRight} " +
                $"face=X{state.Cross}O{state.Circle}Sq{state.Square}Tr{state.Triangle} " +
                $"shoulders=L1{state.L1}R1{state.R1}L3{state.L3}R3{state.R3} " +
                $"sys=Sh{state.Share}Op{state.Options}PS{state.PS}Mute{state.Mute} " +
                $"touchBtn={state.TouchButton}/{state.OutputTouchButton} touch={state.Touch1}/{state.Touch2}/{state.Touch1Finger}/{state.Touch2Fingers} " +
                $"t0={state.TrackPadTouch0.IsActive}:{state.TrackPadTouch0.X},{state.TrackPadTouch0.Y}:{state.TrackPadTouch0.RawTrackingNum} " +
                $"t1={state.TrackPadTouch1.IsActive}:{state.TrackPadTouch1.X},{state.TrackPadTouch1.Y}:{state.TrackPadTouch1.RawTrackingNum} " +
                $"extra=FnL{state.FnL}FnR{state.FnR}BLP{state.BLP}BRP{state.BRP}Cap{state.Capture}SideL{state.SideL}SideR{state.SideR}";
        }

        private static void OSCPostMappingStep(DS4State tempMapState, DS4State oscMapState)
        {
            tempMapState.Cross |= oscMapState.Cross;
            tempMapState.Square |= oscMapState.Square;
            tempMapState.Circle |= oscMapState.Circle;
            tempMapState.Triangle |= oscMapState.Triangle;
            tempMapState.R1 |= oscMapState.R1;
            tempMapState.R3 |= oscMapState.R3;
            tempMapState.L1 |= oscMapState.L1;
            tempMapState.L3 |= oscMapState.L3;
            tempMapState.DpadUp |= oscMapState.DpadUp;
            tempMapState.DpadLeft |= oscMapState.DpadLeft;
            tempMapState.DpadRight |= oscMapState.DpadRight;
            tempMapState.DpadDown |= oscMapState.DpadDown;
            tempMapState.Options |= oscMapState.Options;
            tempMapState.Share |= oscMapState.Share;

            tempMapState.LX = oscMapState.LX != 128 ? oscMapState.LX : tempMapState.LX;
            tempMapState.LY = oscMapState.LY != 128 ? oscMapState.LY : tempMapState.LY;
            tempMapState.L2 = oscMapState.L2 != 0 ? oscMapState.L2 : tempMapState.L2;
            tempMapState.RX = oscMapState.RX != 128 ? oscMapState.RX : tempMapState.RX;
            tempMapState.RY = oscMapState.RY != 128 ? oscMapState.RY : tempMapState.RY;
            tempMapState.R2 = oscMapState.R2 != 0 ? oscMapState.R2 : tempMapState.R2;
        }

        private void OSCPreMappingStep(int ind, DS4State cState, DS4State tempMapState,
            DS4State oscMapState)
        {
            if (cState.Battery != oscMapState.Battery)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + ind + "/battery", Convert.ToInt32(cState.Battery)));
                oscMapState.Battery = cState.Battery;
            }
            cState.Cross |= oscMapState.Cross;
            cState.Square |= oscMapState.Square;
            cState.Circle |= oscMapState.Circle;
            cState.Triangle |= oscMapState.Triangle;
            cState.R1 |= oscMapState.R1;
            cState.R3 |= oscMapState.R3;
            cState.L1 |= oscMapState.L1;
            cState.L3 |= oscMapState.L3;
            cState.DpadUp |= oscMapState.DpadUp;
            cState.DpadLeft |= oscMapState.DpadLeft;
            cState.DpadRight |= oscMapState.DpadRight;
            cState.DpadDown |= oscMapState.DpadDown;
            cState.Options |= oscMapState.Options;
            cState.Share |= oscMapState.Share;

            cState.LX = oscMapState.LX != 128 ? oscMapState.LX : cState.LX;
            cState.LY = oscMapState.LY != 128 ? oscMapState.LY : cState.LY;
            cState.L2 = oscMapState.L2 != 0 ? oscMapState.L2 : cState.L2;
            cState.RX = oscMapState.RX != 128 ? oscMapState.RX : cState.RX;
            cState.RY = oscMapState.RY != 128 ? oscMapState.RY : cState.RY;
            cState.R2 = oscMapState.R2 != 0 ? oscMapState.R2 : cState.R2;

            CompareAndSendChangesToOSC(ind, tempMapState, cState);
        }

        private void CompareAndSendChangesToOSC(int index, DS4State oldState, DS4State newState)
        {
            // Buttons 
            if (oldState.Square != newState.Square)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/square", newState.Square == true ? 1 : 0));
            }

            if (oldState.Triangle != newState.Triangle)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/triangle", newState.Triangle == true ? 1 : 0));
            }

            if (oldState.Circle != newState.Circle)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/circle", newState.Circle == true ? 1 : 0));
            }

            if (oldState.Cross != newState.Cross)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/cross", newState.Cross == true ? 1 : 0));
            }

            if (oldState.DpadUp != newState.DpadUp)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/dpadup", newState.DpadUp == true ? 1 : 0));
            }

            if (oldState.DpadDown != newState.DpadDown)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/dpaddown", newState.DpadDown == true ? 1 : 0));
            }

            if (oldState.DpadLeft != newState.DpadLeft)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/dpadleft", newState.DpadLeft == true ? 1 : 0));
            }

            if (oldState.DpadRight != newState.DpadRight)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/dpadright", newState.DpadRight == true ? 1 : 0));
            }

            if (oldState.L1 != newState.L1)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/l1", newState.L1 == true ? 1 : 0));
            }

            if (oldState.L3 != newState.L3)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/l3", newState.L3 == true ? 1 : 0));
            }

            if (oldState.R1 != newState.R1)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/r1", newState.R1 == true ? 1 : 0));
            }

            if (oldState.R3 != newState.R3)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/r3", newState.R3 == true ? 1 : 0));
            }

            if (oldState.Options != newState.Options)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/options", newState.Options == true ? 1 : 0));
            }

            if (oldState.Share != newState.Share)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/share", newState.Share == true ? 1 : 0));
            }

            if (oldState.PS != newState.PS)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/ps", newState.PS == true ? 1 : 0));
            }

            // Sticks
            if (oldState.LX != newState.LX)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/lx", Convert.ToInt32(newState.LX)));
            }

            if (oldState.LY != newState.LY)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/ly", Convert.ToInt32(newState.LY)));
            }

            if (oldState.RX != newState.RX)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/rx", Convert.ToInt32(newState.RX)));
            }

            if (oldState.RY != newState.RY)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/ry", Convert.ToInt32(newState.RY)));
            }

            // Triggers
            if (oldState.L2 != newState.L2)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/l2", Convert.ToInt32(newState.L2)));
            }

            if (oldState.R2 != newState.R2)
            {
                oscSender.Send(new OscMessage("/ds4windows/monitor/" + index + "/r2", Convert.ToInt32(newState.R2)));
            }

            // if (oldState.Battery != newState.Battery)
            // {
            //     AppLogger.LogToGui("BATTERY " + oldState.Battery + " : " + newState.Battery, false);
            //     oscSender.Send(new SharpOSC.OscMessage("/ds4windows/monitor/" + index + "/battery", Convert.ToInt32(newState.Battery)));
            // }
        }

        private void LagFlashWarning(DS4Device device, int ind, bool on)
        {
            if (on)
            {
                lag[ind] = true;
                LogDebug(string.Format(DS4WinWPF.Properties.Resources.LatencyOverTen, (ind + 1), device.Latency), true);
                if (getFlashWhenLate())
                {
                    DS4Color color = new DS4Color { red = 50, green = 0, blue = 0 };
                    DS4LightBar.forcedColor[ind] = color;
                    DS4LightBar.forcedFlash[ind] = 2;
                    DS4LightBar.forcelight[ind] = true;
                }
            }
            else
            {
                lag[ind] = false;
                LogDebug(DS4WinWPF.Properties.Resources.LatencyNotOverTen.Replace("*number*", (ind + 1).ToString()));
                DS4LightBar.forcelight[ind] = false;
                DS4LightBar.forcedFlash[ind] = 0;
                device.LightBarColor = getMainColor(ind);
            }
        }

        public DS4Controls GetActiveInputControl(int ind)
        {
            DS4State cState = CurrentState[ind];
            DS4StateExposed eState = ExposedState[ind];
            Mouse tp = touchPad[ind];
            DS4Controls result = DS4Controls.None;

            if (DS4Controllers[ind] != null)
            {
                if (Mapping.getBoolButtonMapping(cState.Cross))
                    result = DS4Controls.Cross;
                else if (Mapping.getBoolButtonMapping(cState.Circle))
                    result = DS4Controls.Circle;
                else if (Mapping.getBoolButtonMapping(cState.Triangle))
                    result = DS4Controls.Triangle;
                else if (Mapping.getBoolButtonMapping(cState.Square))
                    result = DS4Controls.Square;
                else if (Mapping.getBoolButtonMapping(cState.L1))
                    result = DS4Controls.L1;
                else if (Mapping.getBoolTriggerMapping(cState.L2))
                    result = DS4Controls.L2;
                else if (Mapping.getBoolButtonMapping(cState.L3))
                    result = DS4Controls.L3;
                else if (Mapping.getBoolButtonMapping(cState.R1))
                    result = DS4Controls.R1;
                else if (Mapping.getBoolTriggerMapping(cState.R2))
                    result = DS4Controls.R2;
                else if (Mapping.getBoolButtonMapping(cState.R3))
                    result = DS4Controls.R3;
                else if (Mapping.getBoolButtonMapping(cState.DpadUp))
                    result = DS4Controls.DpadUp;
                else if (Mapping.getBoolButtonMapping(cState.DpadDown))
                    result = DS4Controls.DpadDown;
                else if (Mapping.getBoolButtonMapping(cState.DpadLeft))
                    result = DS4Controls.DpadLeft;
                else if (Mapping.getBoolButtonMapping(cState.DpadRight))
                    result = DS4Controls.DpadRight;
                else if (Mapping.getBoolButtonMapping(cState.Share))
                    result = DS4Controls.Share;
                else if (Mapping.getBoolButtonMapping(cState.Options))
                    result = DS4Controls.Options;
                else if (Mapping.getBoolButtonMapping(cState.PS))
                    result = DS4Controls.PS;
                else if (Mapping.getBoolAxisDirMapping(cState.LX, true))
                    result = DS4Controls.LXPos;
                else if (Mapping.getBoolAxisDirMapping(cState.LX, false))
                    result = DS4Controls.LXNeg;
                else if (Mapping.getBoolAxisDirMapping(cState.LY, true))
                    result = DS4Controls.LYPos;
                else if (Mapping.getBoolAxisDirMapping(cState.LY, false))
                    result = DS4Controls.LYNeg;
                else if (Mapping.getBoolAxisDirMapping(cState.RX, true))
                    result = DS4Controls.RXPos;
                else if (Mapping.getBoolAxisDirMapping(cState.RX, false))
                    result = DS4Controls.RXNeg;
                else if (Mapping.getBoolAxisDirMapping(cState.RY, true))
                    result = DS4Controls.RYPos;
                else if (Mapping.getBoolAxisDirMapping(cState.RY, false))
                    result = DS4Controls.RYNeg;
                else if (Mapping.getBoolTouchMapping(tp.leftDown))
                    result = DS4Controls.TouchLeft;
                else if (Mapping.getBoolTouchMapping(tp.rightDown))
                    result = DS4Controls.TouchRight;
                else if (Mapping.getBoolTouchMapping(tp.multiDown))
                    result = DS4Controls.TouchMulti;
                else if (Mapping.getBoolTouchMapping(tp.upperDown))
                    result = DS4Controls.TouchUpper;
            }

            return result;
        }

        public bool[] touchreleased = new bool[MAX_DS4_CONTROLLER_COUNT] { true, true, true, true, true, true, true, true },
            touchslid = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };

        public Dispatcher EventDispatcher { get => eventDispatcher; }
        public OutputSlotManager OutputslotMan { get => outputslotMan; }

        protected void CheckForTouchToggle(int deviceID, DS4State cState, DS4State pState)
        {
            if (!IsUsingTouchpadForControls(deviceID) && cState.Touch1 && pState.PS)
            {
                if (GetTouchActive(deviceID) && touchreleased[deviceID])
                {
                    TouchActive[deviceID] = false;
                    LogDebug(DS4WinWPF.Properties.Resources.TouchpadMovementOff);
                    AppLogger.LogToTray(DS4WinWPF.Properties.Resources.TouchpadMovementOff);
                    touchreleased[deviceID] = false;
                }
                else if (touchreleased[deviceID])
                {
                    TouchActive[deviceID] = true;
                    LogDebug(DS4WinWPF.Properties.Resources.TouchpadMovementOn);
                    AppLogger.LogToTray(DS4WinWPF.Properties.Resources.TouchpadMovementOn);
                    touchreleased[deviceID] = false;
                }
            }
            else
                touchreleased[deviceID] = true;
        }

        public void StartTPOff(int deviceID)
        {
            if (deviceID < CURRENT_DS4_CONTROLLER_LIMIT)
            {
                TouchActive[deviceID] = false;
            }
        }

        public void SetTouchpadMovementActive(int deviceID, bool active)
        {
            if (deviceID < CURRENT_DS4_CONTROLLER_LIMIT)
            {
                TouchActive[deviceID] = active;
                touchreleased[deviceID] = true;
            }
        }

        public string TouchpadSlide(int ind)
        {
            DS4State cState = CurrentState[ind];
            string slidedir = "none";
            if (DS4Controllers[ind] != null && cState.Touch2 &&
               !(touchPad[ind].dragging || touchPad[ind].dragging2))
            {
                if (touchPad[ind].slideright && !touchslid[ind])
                {
                    slidedir = "right";
                    touchslid[ind] = true;
                }
                else if (touchPad[ind].slideleft && !touchslid[ind])
                {
                    slidedir = "left";
                    touchslid[ind] = true;
                }
                else if (!touchPad[ind].slideleft && !touchPad[ind].slideright)
                {
                    slidedir = "";
                    touchslid[ind] = false;
                }
            }

            return slidedir;
        }

        public void LogDebug(String Data, bool warning = false)
        {
            //Console.WriteLine(System.DateTime.Now.ToString("G") + "> " + Data);
            if (Debug != null)
            {
                DebugEventArgs args = new DebugEventArgs(Data, warning);
                OnDebug(this, args);
            }
        }

        public static void StartupDiag(string data)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            startupDiagLogger.Info($"[StartupDiag][T{Thread.CurrentThread.ManagedThreadId}] {data}");
            LogManager.Flush(TimeSpan.FromSeconds(1));
        }

        public void OnDebug(object sender, DebugEventArgs args)
        {
            if (Debug != null)
                Debug(this, args);
        }

        // sets the rumble adjusted with rumble boost. General use method
        public void setRumble(byte heavyMotor, byte lightMotor, int deviceNum)
        {
            if (deviceNum < CURRENT_DS4_CONTROLLER_LIMIT)
            {
                DS4Device device = DS4Controllers[deviceNum];
                if (device != null)
                    SetDevRumble(device, heavyMotor, lightMotor, deviceNum);
                //device.setRumble((byte)lightBoosted, (byte)heavyBoosted);
            }
        }

        // sets the rumble adjusted with rumble boost. Method more used for
        // report handling. Avoid constant checking for a device.
        public void SetDevRumble(DS4Device device,
            byte heavyMotor, byte lightMotor, int deviceNum)
        {
            byte boost = getRumbleBoost(deviceNum);
            uint lightBoosted = ((uint)lightMotor * (uint)boost) / 100;
            if (lightBoosted > 255)
                lightBoosted = 255;
            uint heavyBoosted = ((uint)heavyMotor * (uint)boost) / 100;
            if (heavyBoosted > 255)
                heavyBoosted = 255;

            if (Global.InverseRumbleMotors[deviceNum])
                device.setRumble((byte)heavyBoosted, (byte)lightBoosted);
            else
                device.setRumble((byte)lightBoosted, (byte)heavyBoosted);
        }

        public DS4State getDS4State(int ind)
        {
            return CurrentState[ind];
        }

        public DS4State getDS4StateMapped(int ind)
        {
            return MappedState[ind];
        }

        public DS4State getDS4StateTemp(int ind)
        {
            return TempState[ind];
        }
    }
}
