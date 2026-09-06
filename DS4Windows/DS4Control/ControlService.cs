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
using DS4Windows.Switch2;
using static DS4Windows.Global;
using Switch2CemuhookYawPolicy =
    DS4Windows.Switch2.Switch2CemuhookYawSensitivity;

namespace DS4Windows
{
    public class ControlService
    {
        private readonly DualSenseAudioPassthrough dualSenseAudioPassthrough = new DualSenseAudioPassthrough();
        private readonly DualShock4AudioPassthrough dualShock4AudioPassthrough = new DualShock4AudioPassthrough();
        private readonly DualSenseMicrophonePassthrough dualSenseMicrophonePassthrough = new DualSenseMicrophonePassthrough();
        private readonly AudioHapticsService audioHapticsService = new AudioHapticsService();
        private readonly ViiperOutDevice[] playStationFeatureOutputDevices =
            new ViiperOutDevice[MAX_DS4_CONTROLLER_COUNT];
        private readonly object playStationFeatureOutputLock = new object();
        private readonly GameBarIntegration gameBarIntegration = new GameBarIntegration();
        private readonly object hidHideSessionLock = new object();
        private readonly object hidHideDriverMutationLock = new object();
        private readonly HidHideManagedDeviceRegistry<DS4Device>
            hidHideManagedDevices = new HidHideManagedDeviceRegistry<DS4Device>(
                initiallyAcceptingConnections: false);
        private readonly object steamInputReclaimLock = new object();
        private readonly Dictionary<string, DateTime> steamInputReclaimAttempts =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private int hidHideCurrentProcessAccessVerified;
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
        private readonly ControlServiceMouseCallbackRegistry mouseCallbackRegistry = new();
        private readonly int[] mouseCallbackRetirementWarning = new int[MAX_DS4_CONTROLLER_COUNT];
        private const int MouseCallbackRetirementTimeoutMilliseconds = 5_000;
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
        private System.Threading.Timer gameBarStateTimer;
        private int gameBarStateUpdateGate = 0;
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
        Thread eventDispatchThread;
        Dispatcher eventDispatcher;
        public bool suspending;

        private UdpServer _udpServer;
        private readonly UdpMotionObservationWorker switch2UdpObservations;
        private OutputSlotManager outputslotMan;

        private HashSet<string> hidDeviceHidingAffectedDevs = new HashSet<string>();
        private HashSet<string> hidDeviceHidingExemptedDevs = new HashSet<string>();
        private bool hidDeviceHidingForced = false;
        private bool hidDeviceHidingEnabled = false;
        private bool stickMouseFakerInputNoticeShown = false;
        private bool stickMouseFakerInputMissingNoticeShown = false;
        private readonly object outputKbmHandlerLock = new object();
        private readonly object serviceLifecycleLock = new object();
        private readonly InputControllerRegistrationTable inputRegistrationTable;
        private readonly ControlServiceInputSlotAdmission inputSlotAdmission;
        private readonly ControlServiceLegacyHidSlotAuthority
            legacyHidSlotAuthority;
        private readonly Switch2RuntimeRegistrationService
            switch2RuntimeRegistrationService;
        private readonly Switch2ControlServiceReversibleProfileSlotHost
            switch2ControlServiceSlotHost;
        private readonly Switch2BluetoothProductionCoordinator
            switch2BluetoothProductionCoordinator;
        private readonly Switch2ProUsbProductionCoordinator
            switch2ProUsbProductionCoordinator;
        private readonly Switch2JoyConPairFileStore switch2JoyConPairStore;
        private readonly Switch2MagnetometerCalibrationFileStore
            switch2MagnetometerCalibrationStore;
        private readonly Switch2JoyConHoldModeFileStore
            switch2JoyConHoldModeStore;
        private readonly Switch2GyroCalibrationFileStore
            switch2GyroCalibrationStore;
        private readonly Switch2RawStickCalibrationFileStore switch2RawStickCalibrationStore;
        private readonly Switch2PersistentPeerIdentityDeriver
            switch2PersistentPeerIdentityDeriver;
        private CancellationTokenSource switch2BluetoothStartupCancellation;
        private readonly Switch2BluetoothDiscoveryStartupState
            switch2BluetoothDiscoveryStartupState = new();
        private ulong inputRegistrationCloseGeneration;
        private bool exactTypedStopRetryPending;

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
        public event HotplugControllerHandler RemovedController;

        private byte[][] udpOutBuffers = new byte[UdpServer.NUMBER_SLOTS][]
        {
            new byte[UdpServer.DATA_RSP_PACKET_LEN], new byte[UdpServer.DATA_RSP_PACKET_LEN],
            new byte[UdpServer.DATA_RSP_PACKET_LEN], new byte[UdpServer.DATA_RSP_PACKET_LEN],
        };

        private DS4State[] oscState = new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private readonly DS4State[] oscMonitorPreviousState =
            new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private readonly DS4State[] oscMonitorPendingState =
            new DS4State[MAX_DS4_CONTROLLER_COUNT];
        private readonly OscMonitoringWorker oscMonitoringWorker;
        private readonly ReportDiagnosticsWorker reportDiagnosticsWorker;
        private int realtimeWorkersDisposed;
        public HandleOscPacket oscCallback;

        public UDPListener oscListener;
        public UDPSender oscSender;

        void GetPadDetailForIdx(int padIdx, ref DualShockPadMeta meta)
        {
            //meta = new DualShockPadMeta();
            meta.PadId = (byte)padIdx;
            meta.Model = DsModel.DS4;

            var d = DS4Controllers[padIdx];
            if (d is Switch2RuntimeInputDevice)
            {
                // Runtime registration, not a fabricated Sony serial or a
                // current numeric slot, owns Switch 2 DSU identity/status.
                if (switch2UdpObservations != null &&
                    switch2UdpObservations.TryGetMetadata(padIdx, d, out var observed))
                    meta = observed;
                else
                    meta = new DualShockPadMeta { PadId = (byte)padIdx, PadState = DsState.Disconnected };
                return;
            }
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
            inputRegistrationTable =
                new InputControllerRegistrationTable(
                    MAX_DS4_CONTROLLER_COUNT);
            inputSlotAdmission = new ControlServiceInputSlotAdmission(
                inputRegistrationTable, DS4Controllers, slotManager,
                CURRENT_DS4_CONTROLLER_LIMIT);
            legacyHidSlotAuthority =
                new ControlServiceLegacyHidSlotAuthority(
                    inputRegistrationTable, DS4Devices.RemoveDevice,
                    new ControlServiceLegacyHidDeviceWorkerLifecycle());
            switch2RuntimeRegistrationService =
                new Switch2RuntimeRegistrationService(
                    inputRegistrationTable, slotAdmission: inputSlotAdmission);
            switch2RuntimeRegistrationService.RuntimeRemoved +=
                OnSwitch2RuntimeRemoved;
            switch2UdpObservations = new UdpMotionObservationWorker();
            reportDiagnosticsWorker = new ReportDiagnosticsWorker(
                MAX_DS4_CONTROLLER_COUNT, ProcessReportDiagnostics,
                ex => LogDebug("Deferred report diagnostics failed: " +
                    ex.Message));
            switch2ControlServiceSlotHost =
                new Switch2ControlServiceReversibleProfileSlotHost(
                    inputRegistrationTable,
                    switch2RuntimeRegistrationService.LifecycleGate,
                    DS4Controllers, touchPad, slotManager,
                    new Switch2ControlServiceProfileStage(this),
                    On_Report, new Switch2UdpMotionObserver(switch2UdpObservations,
                        () => Volatile.Read(ref _udpServer)?.CurrentSession),
                    reportDiagnosticsWorker, On_Report);

            Switch2JoyConPairFileStore.TryOpen(
                Path.Combine(appdatapath, "Switch2"),
                out switch2JoyConPairStore,
                out switch2PersistentPeerIdentityDeriver);
            Switch2MagnetometerCalibrationFileStore.TryOpen(
                Path.Combine(appdatapath, "Switch2"),
                out switch2MagnetometerCalibrationStore);
            Switch2JoyConHoldModeFileStore.TryOpen(
                Path.Combine(appdatapath, "Switch2"),
                out switch2JoyConHoldModeStore);
            Switch2GyroCalibrationFileStore.TryOpen(
                Path.Combine(appdatapath, "Switch2"),
                out switch2GyroCalibrationStore);
            Switch2RawStickCalibrationFileStore.TryOpen(
                Path.Combine(appdatapath, "Switch2"), out switch2RawStickCalibrationStore);
            switch2BluetoothProductionCoordinator =
                new Switch2BluetoothProductionCoordinator(
                    new Switch2BluetoothWindowsAdapter(
                        new Switch2BluetoothWinRtPlatform(),
                        new Switch2BluetoothCandidateRegistry(),
                        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
                        switch2PersistentPeerIdentityDeriver),
                    switch2RuntimeRegistrationService,
                    switch2ControlServiceSlotHost,
                    OnSwitch2RuntimeAttached,
                    message => LogDebug(message),
                    switch2JoyConPairStore,
                    () => Global.DeviceOptions.JoyConDeviceOpts.
                        AutomaticPairing,
                    switch2MagnetometerCalibrationStore,
                    switch2JoyConHoldModeStore,
                    switch2GyroCalibrationStore, switch2RawStickCalibrationStore);
            switch2ProUsbProductionCoordinator =
                new Switch2ProUsbProductionCoordinator(
                    switch2RuntimeRegistrationService,
                    switch2ControlServiceSlotHost,
                    OnSwitch2RuntimeAttached,
                    message => LogDebug(message),
                    switch2PersistentPeerIdentityDeriver,
                    switch2MagnetometerCalibrationStore,
                    switch2GyroCalibrationStore, switch2RawStickCalibrationStore);

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
                oscMonitorPreviousState[i] = new DS4State();
                oscMonitorPendingState[i] = new DS4State();

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

            outputslotMan = new OutputSlotManager(
                EnsureHidHideDoesNotCloakVirtualSonyOutputs);
            //outputslotMan.SlotAssigned += OutputslotMan_SlotAssigned;
            deviceOptions = Global.DeviceOptions;

            DS4Devices.RequestElevation += DS4Devices_RequestElevation;
            DS4Devices.PrepareDS4Init = PrepareDS4DeviceInit;
            DS4Devices.PostDS4Init = PostDS4DeviceInit;
            DS4Devices.PreparePendingDevice = CheckForSupportedDevice;

            Global.UDPServerSmoothingMincutoffChanged += ChangeUdpSmoothingAttrs;
            Global.UDPServerSmoothingBetaChanged += ChangeUdpSmoothingAttrs;

            CreateOSCCallback();
            oscMonitoringWorker = new OscMonitoringWorker(
                MAX_DS4_CONTROLLER_COUNT, OSCMonitoringPostPublication,
                ex => LogDebug("OSC monitoring output failed: " +
                    ex.Message));
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
            if (ActionMapsToMouse(setting.actionType,
                    setting.action.actionBtn) ||
                ActionMapsToMouse(setting.shiftActionType,
                    setting.shiftAction.actionBtn))
            {
                return true;
            }
            foreach (Switch2ModeShiftScope scope in Enum.GetValues<
                Switch2ModeShiftScope>())
            {
                Switch2ModeShiftAction lane =
                    setting.GetSwitch2ModeShiftAction(scope);
                if (ActionMapsToMouse(lane.ActionType,
                        lane.Action.actionBtn))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool ActionMapsToMouse(
            DS4ControlSettings.ActionType actionType,
            X360Controls outputControl)
        {
            if (actionType != DS4ControlSettings.ActionType.Button)
            {
                return false;
            }

            return (outputControl >= X360Controls.LeftMouse &&
                    outputControl < X360Controls.Unbound) ||
                outputControl is X360Controls.WLEFT or
                    X360Controls.WRIGHT;
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
                    foreach (Switch2ModeShiftScope scope in Enum.GetValues<
                        Switch2ModeShiftScope>())
                    {
                        Global.RefreshSwitch2ModeShiftActionAlias(
                            setting.GetSwitch2ModeShiftAction(scope));
                    }
                }
            }
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
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return;
            lock (serviceLifecycleLock)
            {
                if (exactTypedStopRetryPending) return;
                ShutDownCore();
            }
        }

        public void StopAndShutDown(bool immediateUnplug)
        {
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return;
            lock (serviceLifecycleLock)
            {
                if (running || exactTypedStopRetryPending)
                {
                    if (!StopCore(showlog: true,
                            immediateUnplug: immediateUnplug)) return;
                }

                ShutDownCore();
            }
        }

        private void ShutDownCore()
        {
            hidHideManagedDevices.CloseLifecycle();
            DisposeRealtimeWorkers();
            ReleaseHidHideManagedDevices();
            outputslotMan.ShutDown();
            OutputSlotPersist.WriteConfig(outputslotMan);

            eventDispatcher.InvokeShutdown();
            eventDispatcher = null;

            eventDispatchThread.Join();
            eventDispatchThread = null;
        }

        private void DisposeRealtimeWorkers()
        {
            if (Interlocked.Exchange(ref realtimeWorkersDisposed, 1) != 0)
            {
                return;
            }
            oscMonitoringWorker.Dispose();
            reportDiagnosticsWorker.Dispose();
            switch2UdpObservations?.Dispose();
        }

        private void DS4Devices_RequestElevation(RequestElevationArgs args)
        {
            if (PortableLabContext.IsActive)
            {
                args.StatusCode = RequestElevationArgs.STATUS_INIT_FAILURE;
                return;
            }
            // Launches an elevated child process to re-enable device
            ProcessStartInfo startInfo =
                new ProcessStartInfo(Global.exelocation);
            startInfo.Verb = "runas";
            startInfo.Arguments = "re-enabledevice " + args.InstanceId;
            startInfo.UseShellExecute = true;

            try
            {
                Process child = Process.Start(startInfo);
                if (child == null)
                {
                    return;
                }

                if (!child.WaitForExit(30000))
                {
                    // Never terminate this helper while it may be between the
                    // SetupAPI disable and enable calls.  The helper owns the
                    // recovery path and must be allowed to finish even when a
                    // slow driver stack exceeds the normal wait window.
                    LogDebug("The elevated controller recovery helper is still running; leaving it active so it can safely re-enable the HID device.", true);
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
            if (PortableLabContext.IsActive) return;
            if (!Global.hidHideInstalled)
            {
                return;
            }

            bool checkingCurrentProcess = string.IsNullOrEmpty(ExePath);
            LogDebug("HidHide control device found");
            lock (hidHideDriverMutationLock)
            {
                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                {
                    if (!hidHideDevice.IsOpen())
                    {
                        return;
                    }

                    // Catch Blank Values and initialize for Startup. Also catches empty Values.
                    // Also Catches Empty values in auto-profiler, and defaults to trying to re-add D4W. Will fail harmlessly later.
                    if (ExePath == "") { ExePath = Global.exelocation; ExeName = "DS4Windows"; AddExe = true; }

                    if (!hidHideDevice.TryGetWhitelistInverseState(
                            out bool inverseAppCloak))
                    {
                        StartupDiag("Could not read the HidHide application-list mode; leaving its policy unchanged");
                        return;
                    }

                    if (!hidHideDevice.TryGetWhitelist(
                            out List<string> dosPaths))
                    {
                        StartupDiag("Could not read the HidHide whitelist; leaving it unchanged");
                        return;
                    }

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
                        FileSystemInfo target = dirInfo.ResolveLinkTarget(
                            returnFinalTarget: true);
                        if (target is DirectoryInfo targetDirectory)
                        {
                            ExePath = Path.Combine(targetDirectory.FullName,
                                Path.GetFileName(ExePath));
                        }
                    }

                    string pathRoot = Path.GetPathRoot(ExePath);
                    if (string.IsNullOrWhiteSpace(pathRoot)) return;
                    string driveLetter = pathRoot.TrimEnd('\\');
                    uint pathLength = NativeMethods.QueryDosDevice(driveLetter,
                        sb, maxPathCheckLength);
                    if (pathLength == 0) return;

                    string dosDrivePath = sb.ToString();
                    // Strip a possible \??\ prefix.
                    if (dosDrivePath.StartsWith(@"\??\"))
                    {
                        dosDrivePath = dosDrivePath.Remove(0, 4);
                    }

                    string partial = ExePath.Substring(pathRoot.Length);
                    // Need to trim starting '\\' from path2 or Path.Combine will
                    // treat it as an absolute path and only return path2
                    string realPath = Path.Combine(dosDrivePath, partial.TrimStart('\\'));
                    bool exists = dosPaths.Contains(realPath,
                        StringComparer.OrdinalIgnoreCase);

                    // In inverse mode the application list is the deny list.
                    // Do not change user policy, but only authorize automatic
                    // device hiding when DS4Windows is proven absent from it.
                    if (inverseAppCloak)
                    {
                        if (checkingCurrentProcess && !exists)
                        {
                            Volatile.Write(
                                ref hidHideCurrentProcessAccessVerified, 1);
                        }
                        return;
                    }

                    if (!exists && AddExe)
                    {
                        LogDebug($"{ExeName} not found in HidHide whitelist. Adding to list");
                        dosPaths.Add(realPath);
                        exists = hidHideDevice.SetWhitelist(dosPaths);
                    }
                    if (exists && !AddExe)
                    {
                        LogDebug($"{ExeName} found in HidHide whitelist. Removing from list");
                        dosPaths.RemoveAll(path => string.Equals(path, realPath,
                            StringComparison.OrdinalIgnoreCase));
                        hidHideDevice.SetWhitelist(dosPaths);
                    }

                    if (checkingCurrentProcess && AddExe && exists)
                    {
                        Volatile.Write(ref hidHideCurrentProcessAccessVerified,
                            1);
                    }
                }
            }
        }

        // Cold prepared application only. Do not wait with publication paused:
        // queued profile work can itself be switching the shared KBM backend.
        // Backend switching must not acquire ProfileMutationGate under this lock.
        internal bool TryRunWithStableProfileKbmMapping(Action apply)
        {
            if (!Monitor.TryEnter(outputKbmHandlerLock))
                return false;
            try
            {
                if (Global.outputKBMMapping == null)
                    return false;
                apply();
                return true;
            }
            finally { Monitor.Exit(outputKbmHandlerLock); }
        }

        public void LoadPermanentSlotsConfig()
        {
            OutputSlotPersist.ReadConfig(outputslotMan);
        }

        public void UpdateHidHideAttributes()
        {
            if (Global.hidHideInstalled)
            {
                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice(writeAccess: false))
                {
                    if (!hidHideDevice.IsOpen())
                    {
                        return;
                    }

                    if (!hidHideDevice.TryGetActiveState(out bool active) ||
                        !hidHideDevice.TryGetBlacklist(
                            out List<string> instances))
                    {
                        // CheckAffected consumes this cache while controllers
                        // reconnect. A transient query failure must not turn a
                        // previously proven HidHide policy into "disabled".
                        StartupDiag("Could not refresh HidHide attributes; retaining the last verified policy snapshot");
                        return;
                    }

                    hidDeviceHidingAffectedDevs.Clear();
                    hidDeviceHidingExemptedDevs.Clear(); // No known equivalent in HidHide
                    hidDeviceHidingForced = false; // No known equivalent in HidHide
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
                case InputDevices.InputDeviceType.Switch2Pro:
                    // GL/GR use the canonical paddle sources. Decoding them
                    // is not enough: these controls are deliberately outside
                    // ControlSettingsGroup's ordinary button loop.
                    result.AddRange(new[] { DS4Controls.Capture,
                        DS4Controls.BLP, DS4Controls.BRP });
                    break;
                case InputDevices.InputDeviceType.Switch2JoyConLeft:
                case InputDevices.InputDeviceType.Switch2JoyConJoined:
                    // The right Joy-Con has no Capture button. Its C/rail
                    // sources already belong to the standard mapping loop.
                    result.Add(DS4Controls.Capture);
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
        /// Adds the device to HidHide while the DS4Windows service is running.
        /// Stop releases managed entries and Start acquires them again.
        /// </summary>
        private bool EnsureHidHideSessionForDevice(DS4Device dev)
        {
            if (PortableLabContext.IsActive) return false;
            if (!Global.hidHideInstalled || dev == null) return false;

            HidDevice hidDevice = dev.HidDevice;
            if (hidDevice == null ||
                string.IsNullOrWhiteSpace(hidDevice.DevicePath))
            {
                // Transport-owned logical devices (Switch 2 WinUSB/WinRT)
                // intentionally do not expose a legacy HID handle. HidHide
                // has no DS4Device HID identity to manage for that lifetime.
                return false;
            }

            // Never cloak a controller unless this process has first proved
            // that it can see through HidHide.  Without this gate the initial
            // already-open handle works, but the next wired PnP generation is
            // hidden from DS4Windows itself and appears permanently dead.
            if (Volatile.Read(ref hidHideCurrentProcessAccessVerified) == 0)
            {
                CheckHidHidePresence();
                if (Volatile.Read(
                        ref hidHideCurrentProcessAccessVerified) == 0)
                {
                    StartupDiag("HidHide controller containment skipped because DS4Windows whitelist access could not be verified");
                    return false;
                }
            }

            string instanceId = Global.GetInstanceIdFromDevicePath(
                hidDevice.DevicePath);
            if (string.IsNullOrEmpty(instanceId)) return false;

            IReadOnlyList<string> instanceIds =
                HidHideDeviceIdentity.Resolve(instanceId);
            if (!hidHideManagedDevices.TryBeginConnection(dev, instanceIds,
                    out HidHideConnectionClaim<DS4Device> claim))
            {
                return false;
            }

            bool connectionEstablished = false;
            try
            {
                lock (hidHideDriverMutationLock)
                {
                    if (!hidHideManagedDevices.IsCurrent(claim)) return false;

                    if (claim.SupersededPersistentReleaseIds.Count > 0)
                    {
                        ReleasePersistentHidHideIds(
                            claim.SupersededPersistentReleaseIds,
                            "superseded controller identity");
                        if (!hidHideManagedDevices.IsCurrent(claim))
                        {
                            return false;
                        }
                    }

                    using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                    {
                        if (!hidHideDevice.IsOpen() ||
                            !hidHideDevice.TryGetBlacklist(
                                out List<string> persistentBlacklist))
                        {
                            return false;
                        }

                        if (!hidHideDevice.TryGetActiveState(out bool active))
                        {
                            StartupDiag($"HidHide active-state query failed for {dev.DisplayName} ({instanceId}); containment was not changed");
                            return false;
                        }

                        lock (hidHideSessionLock)
                        {
                            hidHideActiveStateBeforeManagedSession ??= active;
                        }

                        if (!active && !hidHideDevice.SetActiveState(true))
                        {
                            StartupDiag($"HidHide failed to enable cloaking for {dev.DisplayName} ({instanceId})");
                            return false;
                        }

                        IReadOnlyList<string> uncoveredIds =
                            hidHideManagedDevices.GetUncoveredIds(claim,
                                persistentBlacklist);
                        IReadOnlyList<string> addedSessionIds =
                            Array.Empty<string>();
                        IReadOnlyList<string> addedPersistentIds =
                            Array.Empty<string>();

                        if (uncoveredIds.Count > 0)
                        {
                            List<string> additions = uncoveredIds.ToList();
                            if (hidHideDevice.AddSessionBlacklist(additions))
                            {
                                addedSessionIds = additions;
                                LogDebug($"HidHide session hiding enabled for {dev.DisplayName} ({string.Join(", ", additions)})", false);
                            }
                            else
                            {
                                persistentBlacklist.AddRange(additions.Where(id =>
                                    !persistentBlacklist.Contains(id,
                                        StringComparer.OrdinalIgnoreCase)));
                                if (!hidHideDevice.SetBlacklist(persistentBlacklist))
                                {
                                    StartupDiag($"HidHide persistent blacklist fallback failed for {dev.DisplayName} ({instanceId})");
                                    return false;
                                }

                                addedPersistentIds = additions;
                                LogDebug($"HidHide persistent hiding enabled for {dev.DisplayName} ({string.Join(", ", additions)})", false);
                            }
                        }

                        IReadOnlyList<string> rollbackIds =
                            hidHideManagedDevices.CompleteConnection(claim,
                                addedSessionIds, addedPersistentIds);
                        if (rollbackIds.Count > 0)
                        {
                            ReleasePersistentHidHideIds(rollbackIds,
                                "cancelled connection");
                        }

                        if (!hidHideManagedDevices.IsCurrent(claim))
                        {
                            return false;
                        }

                        UpdateHidHideAttributes();
                        connectionEstablished = true;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"HidHide session setup failed for {dev.DisplayName}: {ex.Message}", true);
                return false;
            }
            finally
            {
                if (!connectionEstablished)
                {
                    // Remove only this claim generation. A newer reconnect for
                    // the same object is never cancelled by an older failure.
                    hidHideManagedDevices.CancelConnection(claim);
                }
            }
        }

        private void ReleaseHidHideManagedDevice(DS4Device device)
        {
            HidHideDisconnectPlan plan = hidHideManagedDevices.Disconnect(device);
            if (plan.BoundInstanceIds.Count == 0) return;

            lock (steamInputReclaimLock)
            {
                foreach (string instanceId in plan.BoundInstanceIds)
                {
                    steamInputReclaimAttempts.Remove(instanceId);
                }
            }

            ReleasePersistentHidHideIds(plan.PersistentReleaseIds,
                "physical disconnect");
        }

        private void ReleasePersistentHidHideIds(
            IEnumerable<string> candidateIds, string reason)
        {
            if (PortableLabContext.IsActive) return;
            IReadOnlyList<string> candidates =
                hidHideManagedDevices.RevalidatePersistentRelease(candidateIds);
            if (candidates.Count == 0) return;

            lock (hidHideDriverMutationLock)
            {
                candidates = hidHideManagedDevices
                    .RevalidatePersistentRelease(candidates);
                if (candidates.Count == 0) return;

                using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                {
                    if (!hidHideDevice.IsOpen() ||
                        !hidHideDevice.TryGetBlacklist(
                            out List<string> blacklist))
                    {
                        StartupDiag($"HidHide could not read its blacklist while releasing {reason}; cleanup will be retried");
                        return;
                    }

                    int removed = blacklist.RemoveAll(item =>
                        candidates.Contains(item,
                            StringComparer.OrdinalIgnoreCase));
                    if (removed > 0 && !hidHideDevice.SetBlacklist(blacklist))
                    {
                        StartupDiag($"HidHide failed to release {removed} {reason} entr{(removed == 1 ? "y" : "ies")}; cleanup will be retried");
                        return;
                    }

                    IReadOnlyList<string> reassertIds =
                        hidHideManagedDevices.CompletePersistentRelease(
                            candidates);
                    if (reassertIds.Count > 0)
                    {
                        foreach (string instanceId in reassertIds)
                        {
                            if (!blacklist.Contains(instanceId,
                                    StringComparer.OrdinalIgnoreCase))
                            {
                                blacklist.Add(instanceId);
                            }
                        }

                        bool reasserted = hidHideDevice.SetBlacklist(blacklist);
                        hidHideManagedDevices.CompletePersistentReassert(
                            reassertIds, reasserted);
                        if (!reasserted)
                        {
                            StartupDiag($"HidHide failed to reassert a controller that reconnected during {reason}; its connection setup will retry the cloak");
                        }
                    }

                    if (removed > 0)
                    {
                        StartupDiag($"Released {removed} DS4Windows-managed HidHide {reason} entr{(removed == 1 ? "y" : "ies")}");
                    }
                    UpdateHidHideAttributes();
                }
            }
        }

        private void QueueSteamInputReclaim(DS4Device device)
        {
            if (PortableLabContext.IsActive) return;
            if (!Global.ReclaimSteamInput || !Global.hidHideInstalled ||
                device?.CurrentExclusiveStatus !=
                    DS4Device.ExclusiveStatus.HidHideAffected ||
                !IsSteamClientRunning())
            {
                return;
            }

            // Restarting a wired HID collection after StartUpdate has taken
            // input/output ownership tears down the live PnP generation.  On
            // some DS4 stacks it comes back as a generic HID collection and
            // races both the captured HidHide identity and DS4Devices' path /
            // serial registries.  HidHide containment is already active here;
            // require a physical reconnect (or Steam restart) instead of
            // restarting an owned wired controller underneath the reader.
            if (!ShouldRestartDeviceForSteamReclaim(
                    device.getConnectionType()))
            {
                LogDebug("Automatic Steam Input reclaim skipped for a wired controller because restarting an active HID collection can strand its reconnect. Reconnect the controller after enabling HidHide if Steam already held it.", true);
                return;
            }

            string instanceId = Global.GetInstanceIdFromDevicePath(
                device.HidDevice.DevicePath);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            lock (steamInputReclaimLock)
            {
                if (steamInputReclaimAttempts.TryGetValue(instanceId,
                        out DateTime previousAttempt) &&
                    now - previousAttempt < TimeSpan.FromSeconds(10))
                {
                    return;
                }

                steamInputReclaimAttempts[instanceId] = now;
            }

            string displayName = device.DisplayName;
            Task task = Task.Run(async () =>
            {
                bool success = false;
                try
                {
                    if (!Global.IsAdministrator())
                    {
                        LogDebug("Steam Input reclaim requires DS4Windows to " +
                            "run as administrator.", true);
                        return;
                    }

                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory,
                            "pnputil.exe"),
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    startInfo.ArgumentList.Add("/restart-device");
                    startInfo.ArgumentList.Add(instanceId);

                    using Process process = Process.Start(startInfo);
                    if (process == null)
                    {
                        return;
                    }

                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    using CancellationTokenSource timeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch { }
                        LogDebug($"Steam Input reclaim timed out for " +
                            $"{displayName}.", true);
                        return;
                    }

                    string output = await outputTask.ConfigureAwait(false);
                    string error = await errorTask.ConfigureAwait(false);
                    success = process.ExitCode == 0;
                    if (success)
                    {
                        LogDebug($"Reclaimed {displayName} from Steam " +
                            "Input; reconnecting its hidden HID collection.",
                            false);
                    }
                    else
                    {
                        string detail = string.IsNullOrWhiteSpace(error)
                            ? output : error;
                        LogDebug($"Steam Input reclaim failed for " +
                            $"{displayName}: {detail.Trim()}", true);
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Steam Input reclaim failed for " +
                        $"{displayName}: {ex.Message}", true);
                }
                finally
                {
                    if (!success)
                    {
                        lock (steamInputReclaimLock)
                        {
                            steamInputReclaimAttempts.Remove(instanceId);
                        }
                    }
                }
            });
            Util.LogAssistBackgroundTask(task);
        }

        internal static bool ShouldRestartDeviceForSteamReclaim(
            ConnectionType connectionType)
        {
            return connectionType != ConnectionType.USB;
        }

        private static bool IsSteamClientRunning()
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("steam");
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (Process process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private void ReleaseHidHideManagedDevices()
        {
            if (PortableLabContext.IsActive) return;
            if (!Global.hidHideInstalled) return;

            try
            {
                lock (hidHideDriverMutationLock)
                {
                    // Close admission, snapshot all process-owned IDs, and
                    // invalidate pre-stop claims while no HidHide IOCTL can
                    // run. An already-entered HotPlug is generation-fenced;
                    // a later Start cannot reopen admission until Stop drops
                    // serviceLifecycleLock after this cleanup completes.
                    HidHideServiceReleasePlan releasePlan =
                        hidHideManagedDevices.BeginServiceRelease();
                    IReadOnlyList<string> sessionIds =
                        releasePlan.SessionIds;
                    IReadOnlyList<string> persistentIds =
                        releasePlan.PersistentIds;

                    bool? restoreActiveState;
                    lock (hidHideSessionLock)
                    {
                        restoreActiveState =
                            hidHideActiveStateBeforeManagedSession;
                    }

                    if (sessionIds.Count == 0 &&
                        persistentIds.Count == 0 &&
                        restoreActiveState is null)
                    {
                        return;
                    }

                    using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                    {
                        if (!hidHideDevice.IsOpen())
                        {
                            StartupDiag("Could not open HidHide while releasing managed controllers; cleanup will be retried");
                            return;
                        }

                        bool sessionReleased = sessionIds.Count == 0;
                        if (sessionIds.Count > 0)
                        {
                            // This IOCTL clears only this process's session
                            // entries.  It is intentionally service-wide and
                            // is never issued for a single controller removal.
                            sessionReleased = hidHideDevice.ClearSessionBlacklist();
                            hidHideManagedDevices.CompleteSessionRelease(
                                sessionIds, sessionReleased);
                            StartupDiag(sessionReleased
                                ? $"Released {sessionIds.Count} DS4Windows-managed HidHide session entries"
                                : "HidHide session release failed; cleanup will be retried");
                        }

                        bool persistentReleased = persistentIds.Count == 0;
                        if (persistentIds.Count > 0)
                        {
                            if (!hidHideDevice.TryGetBlacklist(
                                    out List<string> instances))
                            {
                                StartupDiag("HidHide blacklist read failed while releasing managed controllers; cleanup will be retried");
                            }
                            else
                            {
                                int removed = instances.RemoveAll(item =>
                                    persistentIds.Contains(item,
                                        StringComparer.OrdinalIgnoreCase));

                                persistentReleased = removed == 0 ||
                                    hidHideDevice.SetBlacklist(instances);
                                if (persistentReleased)
                                {
                                    IReadOnlyList<string> reassertIds =
                                        hidHideManagedDevices
                                            .CompletePersistentRelease(
                                                persistentIds);
                                    // A concurrent Start is not expected under
                                    // serviceLifecycleLock, but retain the same
                                    // generation-safe contract as hot-unplug.
                                    if (reassertIds.Count > 0)
                                    {
                                        foreach (string id in reassertIds)
                                        {
                                            if (!instances.Contains(id,
                                                    StringComparer.OrdinalIgnoreCase))
                                            {
                                                instances.Add(id);
                                            }
                                        }
                                        bool reasserted =
                                            hidHideDevice.SetBlacklist(instances);
                                        hidHideManagedDevices
                                            .CompletePersistentReassert(
                                                reassertIds, reasserted);
                                        persistentReleased &= reasserted;
                                    }
                                }

                                if (removed > 0 && persistentReleased)
                                {
                                    StartupDiag($"Released {removed} DS4Windows-managed HidHide blacklist entries");
                                }
                                else if (!persistentReleased)
                                {
                                    StartupDiag("HidHide persistent blacklist release failed; cleanup will be retried");
                                }
                            }
                        }

                        bool activeStateRestored = restoreActiveState != false;
                        if (restoreActiveState == false)
                        {
                            // A late hotplug claim can be registered while the
                            // service-wide driver cleanup is in flight. Never
                            // disable cloaking under that new generation; its
                            // Ensure call will finish after this mutation lock.
                            bool hasLateConnection =
                                hidHideManagedDevices.HasConnections ||
                                hidHideManagedDevices.HasOwnedIds;
                            activeStateRestored = !hasLateConnection &&
                                hidHideDevice.SetActiveState(false);
                            if (!activeStateRestored && !hasLateConnection)
                            {
                                StartupDiag("HidHide cloaking state restore failed; cleanup will be retried");
                            }
                        }

                        lock (hidHideSessionLock)
                        {
                            if (activeStateRestored &&
                                !hidHideManagedDevices.HasOwnedIds)
                            {
                                hidHideActiveStateBeforeManagedSession = null;
                            }
                        }

                        UpdateHidHideAttributes();
                    }
                }
            }
            catch (Exception ex)
            {
                StartupDiag($"ReleaseHidHideManagedDevices exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void EnsureHidHideForVirtualOutput(int index, DS4Device device, OutContType contType)
        {
            contType = contType.Normalize();
            if (device == null || !DS4Devices.isExclusiveMode)
            {
                return;
            }

            if (!ViiperOutDevice.IsViiperType(contType))
            {
                return;
            }

            if (device.HidDevice == null)
            {
                StartupDiag($"HidHide legacy-HID containment is not " +
                    $"applicable to transport-owned {device.DisplayName} " +
                    $"input index={index}");
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

        /// <summary>
        /// A VIIPER Sony output is a complete USB/IP HID, so an instance path
        /// accidentally retained in HidHide's persistent blacklist makes the
        /// virtual controller healthy and writable inside DS4Windows while it
        /// is invisible to games. Remove only the exact before/after paths that
        /// this process just created; physical Sony controllers stay cloaked.
        /// </summary>
        private void EnsureHidHideDoesNotCloakVirtualSonyOutputs(
            IReadOnlyCollection<string> devicePaths)
        {
            if (PortableLabContext.IsActive) return;
            if (!Global.hidHideInstalled || devicePaths == null ||
                devicePaths.Count == 0)
            {
                return;
            }

            HashSet<string> instanceIds = new HashSet<string>(
                devicePaths.Select(Global.GetInstanceIdFromDevicePath)
                    .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId)),
                StringComparer.OrdinalIgnoreCase);
            if (instanceIds.Count == 0)
            {
                return;
            }

            try
            {
                lock (hidHideDriverMutationLock)
                {
                    using (HidHideAPIDevice hidHideDevice = new HidHideAPIDevice())
                    {
                        if (!hidHideDevice.IsOpen() ||
                            !hidHideDevice.TryGetBlacklist(
                                out List<string> blacklist))
                        {
                            StartupDiag(
                                "Could not read HidHide while exempting a VIIPER virtual Sony output");
                            return;
                        }

                        int removed = blacklist.RemoveAll(item =>
                            instanceIds.Contains(item));
                        if (removed == 0)
                        {
                            return;
                        }

                        if (!hidHideDevice.SetBlacklist(blacklist))
                        {
                            StartupDiag(
                                $"HidHide failed to exempt {removed} VIIPER virtual Sony output entr{(removed == 1 ? "y" : "ies")}");
                            return;
                        }

                        hidHideManagedDevices.ForgetPersistentOwnership(
                            instanceIds);

                        StartupDiag(
                            $"HidHide exempted {removed} VIIPER virtual Sony output entr{(removed == 1 ? "y" : "ies")}: {string.Join(", ", instanceIds)}");
                        UpdateHidHideAttributes();
                    }
                }
            }
            catch (Exception ex)
            {
                StartupDiag(
                    $"HidHide VIIPER virtual-output exemption failed {ex.GetType().Name}: {ex.Message}");
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
                oscMonitoringWorker.Resume();
            }
            else
            {
                AppLogger.LogToGui("OSC SENDER STOPPED", false);
                oscMonitoringWorker.Pause();
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
                        RemoveDevUDPMotion(dev);
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
                    SetDevUDPMotionSubscription(dev, subscribe: false);
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
                        SetDevUDPMotionSubscription(dev, subscribe: true);
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
            contType = contType.Normalize();
            StartupDiag($"EstablishOutDevice begin index={index} contType={contType}");
            OutputDevice temp = outputslotMan.AllocateController(contType);
            StartupDiag($"EstablishOutDevice end index={index} contType={contType} result={temp?.GetType().Name ?? "null"}");
            return temp;
        }

        public void AttachNewUnboundOutDev(OutContType contType)
        {
            contType = contType.Normalize();
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
            contType = contType.Normalize();
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

        public void PluginOutDev(int index, DS4Device device,
            OutContType requestedContType = OutContType.None)
        {
            Switch2ControlServiceProfileStageInverse ownership =
                BeginSwitch2OutputOwnershipChange(index, device);
            OutputDevice produced = outputDevices[index];
            bool completed = false;
            try
            {
                PluginOutDevCore(index, device, requestedContType, ref produced);
                completed = true;
            }
            finally
            {
                // Only the exact object allocated/rebound below may become
                // this input lifetime's cleanup responsibility. Failed creation
                // before publication may legitimately leave the slot empty
                // only when creation returned normally. A connected candidate
                // lost before publication remains uncertain on a thrown path.
                ownership?.CompleteOutputChange(produced,
                    allowUnpublishedNull: completed, operationSucceeded: true);
            }
        }

        private void PluginOutDevCore(int index, DS4Device device,
            OutContType requestedContType, ref OutputDevice produced)
        {
            OutContType contType = requestedContType == OutContType.None ?
                Global.OutContType[index].Normalize() :
                requestedContType.Normalize();
            if (requestedContType == OutContType.None)
            {
                Global.OutContType[index] = contType;
            }
            Global.outDevTypeTemp[index] = Global.outDevTypeTemp[index].Normalize();
            StartupDiag($"PluginOutDev enter index={index} contType={contType} useDInputOnly={useDInputOnly[index]} profileDInputOnly={getDInputOnly(index)}");

            OutSlotDevice slotDevice = null;
            if (!getDInputOnly(index))
            {
                slotDevice = outputslotMan.FindExistUnboundSlotType(contType);
                StartupDiag($"PluginOutDev existingSlot index={index} found={slotDevice != null} slot={(slotDevice != null ? slotDevice.Index + 1 : 0)}");
            }

            if (useDInputOnly[index])
            {
                var outputAttempt = new ControllerVirtualOutputAttempt(device, contType);
                Volatile.Write(ref virtualOutputAttempts[index], outputAttempt);
                try
                {
                EnsureHidHideForVirtualOutput(index, device, contType);

                bool success = false;
                if (ViiperOutDevice.IsViiperType(contType))
                {
                    activeOutDevType[index] = contType;
                    if (slotDevice != null)
                    {
                        if (outputslotMan.TryBindExistingUnboundOutput(slotDevice,
                                outputDevices, index, $"{device.DisplayName} [{device.MacAddress}]",
                                contType, out produced))
                        {
                            success = true;
                        }
                        else
                            slotDevice = null;
                    }
                    if (slotDevice == null)
                    {
                        slotDevice = outputslotMan.FindOpenSlot();
                        if (slotDevice != null)
                        {
                            OutputDevice tempViiper = EstablishOutDevice(index, contType);
                            produced = tempViiper;
                            slotDevice = outputslotMan.DeferredPlugin(tempViiper, index,
                                $"{device.DisplayName} [{device.MacAddress}]", outputDevices, contType);
                            success = slotDevice != null;
                        }
                        else
                        {
                            LogDebug("Failed. No open output slot found");
                        }
                    }
                }

                if (success && ReferenceEquals(slotDevice?.OutputDevice, produced) &&
                    ReferenceEquals(outputDevices[index], produced) &&
                    outputslotMan.IsExactBoundOutput(produced, index))
                {
                    LogDebug($"Associated input controller #{index + 1} ({device.DisplayName}) to virtual {slotDevice.CurrentType.ToDisplayName()} Controller in{(slotDevice.PermanentType != OutContType.None ? " permanent" : "")} output slot #{slotDevice.Index + 1}");
                    useDInputOnly[index] = false;
                    StartupDiag($"PluginOutDev success index={index} slot={slotDevice.Index + 1} output={slotDevice.OutputDevice.GetDeviceType()}");
                    Interlocked.CompareExchange(ref virtualOutputAttempts[index], null, outputAttempt);
                }
                else
                {
                    outputAttempt.MarkFailed();
                    LogDebug("Failed. No output device was associated");
                    StartupDiag($"PluginOutDev failed index={index} success={success} slotNull={slotDevice == null} slotOutputNull={slotDevice?.OutputDevice == null}");
                }
                }
                catch
                {
                    // Published output ownership remains independently retained
                    // for cleanup, but an interrupted creation is not Ready.
                    outputAttempt.MarkFailed();
                    throw;
                }
            }
            else
            {
                StartupDiag($"PluginOutDev skipped index={index} useDInputOnly=false");
            }
        }

        public void UnplugOutDev(int index, DS4Device device, bool immediate = false, bool force = false)
        {
            Switch2ControlServiceProfileStageInverse ownership =
                BeginSwitch2OutputOwnershipChange(index, device);
            bool completed = false;
            try
            {
                UnplugOutDevCore(index, device, immediate, force);
                completed = true;
            }
            finally
            {
                // Null-after-throw is not evidence of a successful detach:
                // legacy removal clears its array before Disconnect returns.
                ownership?.CompleteOutputChange(null,
                    allowUnpublishedNull: false, operationSucceeded: completed);
            }
        }

        private void UnplugOutDevCore(int index, DS4Device device, bool immediate, bool force)
        {
            if (device is Switch2RuntimeInputDevice && outputDevices[index] != null &&
                (useDInputOnly[index] ||
                    !outputslotMan.IsExactBoundOutput(outputDevices[index], index)))
                throw new InvalidOperationException("Switch 2 output has no exact active manager binding; removal was rejected.");
            if (!useDInputOnly[index])
            {
                bool preserveUncertainPermanentBinding = false;
                try
                {
                    //OutContType contType = Global.OutContType[index];
                    OutputDevice dev = outputDevices[index];
                    OutSlotDevice slotDevice = outputslotMan.GetOutSlotDevice(dev);
                    if (dev != null && slotDevice != null)
                    {
                        string tempType = slotDevice.CurrentType.ToDisplayName();
                        LogDebug($"Disassociated virtual {tempType} Controller in{(slotDevice.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Permanent ? " permanent" : "")} output slot #{slotDevice.Index + 1} from input controller #{index + 1} ({device.DisplayName})", false);

                        if ((slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached &&
                            slotDevice.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Dynamic) || force)
                        {
                            outputDevices[index] = null;
                            activeOutDevType[index] = OutContType.None;
                            //slotDevice.CurrentInputBound = OutSlotDevice.InputBound.Unbound;
                            outputslotMan.DeferredRemoval(dev, index, outputDevices, immediate);
                        }
                        else if (slotDevice.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached)
                        {
                            // Keep the exact binding private until neutral and
                            // old feedback cleanup finish. Another controller
                            // may reserve it only after the manager publishes
                            // the completed unbind under its existing lock.
                            preserveUncertainPermanentBinding = true;
                            if (!outputslotMan.TryReleaseBoundOutput(dev, outputDevices, index))
                                throw new InvalidOperationException("The permanent output binding changed before release.");
                            preserveUncertainPermanentBinding = false;
                        }
                        //dev.Disconnect();
                        //LogDebug(tempType + " Controller # " + (index + 1) + " unplugged");
                    }
                }
                finally
                {
                    if (!preserveUncertainPermanentBinding)
                    {
                        outputDevices[index] = null;
                        activeOutDevType[index] = OutContType.None;
                        useDInputOnly[index] = true;
                    }
                }
            }
        }

        public bool Start(bool showlog = true)
        {
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
            lock (serviceLifecycleLock)
            {
                if (running)
                {
                    StartupDiag("ControlService.Start ignored because the service is already running");
                    return true;
                }
                if (exactTypedStopRetryPending)
                {
                    StartupDiag("ControlService.Start rejected while exact typed legacy retirement requires a Stop retry");
                    return false;
                }

                return StartCore(showlog);
            }
        }

        private Switch2ControlServiceProfileStageInverse[] switch2ProfileOutputOwners;

        private Switch2ControlServiceProfileStageInverse BeginSwitch2OutputOwnershipChange(
            int index, DS4Device device)
        {
            if (device is not Switch2RuntimeInputDevice) return null;
            object gate = switch2RuntimeRegistrationService?.LifecycleGate;
            if (gate == null)
                throw new InvalidOperationException("Switch 2 output change has no exact input lifetime.");
            lock (gate)
            {
                Switch2ControlServiceProfileStageInverse ownership =
                    switch2ProfileOutputOwners != null &&
                    (uint)index < switch2ProfileOutputOwners.Length ?
                        switch2ProfileOutputOwners[index] : null;
                if (ownership == null || !ownership.TryBeginOutputChange(device))
                    throw new InvalidOperationException("Switch 2 output ownership changed; the output operation was rejected.");
                return ownership;
            }
        }

        private void BeginSwitch2BluetoothDiscovery(
            ulong inputServiceGeneration)
        {
            switch2BluetoothStartupCancellation?.Cancel();
            switch2BluetoothStartupCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            switch2BluetoothStartupCancellation = cancellation;
            Switch2BluetoothDiscoveryStatus starting = switch2BluetoothDiscoveryStartupState.Begin();
            _ = StartSwitch2BluetoothDiscoveryAsync(inputServiceGeneration,
                cancellation, starting);
        }

        private async Task StartSwitch2BluetoothDiscoveryAsync(
            ulong inputServiceGeneration,
            CancellationTokenSource exactCancellation,
            Switch2BluetoothDiscoveryStatus exactStartingStatus)
        {
            try
            {
                byte[] hostAddress = await Switch2BluetoothWinRtPlatform.
                    GetDefaultHostAddressAsync(exactCancellation.Token).
                    ConfigureAwait(false);
                if (exactCancellation.IsCancellationRequested)
                    return;
                if (hostAddress == null)
                {
                    switch2BluetoothDiscoveryStartupState.TryComplete(exactStartingStatus,
                        Switch2BluetoothDiscoveryState.Unavailable);
                    StartupDiag("Switch 2 Bluetooth discovery unavailable because Windows reported no default Bluetooth adapter");
                    return;
                }

                lock (serviceLifecycleLock)
                {
                    if (!running || exactCancellation.IsCancellationRequested ||
                        !ReferenceEquals(switch2BluetoothStartupCancellation,
                            exactCancellation) ||
                        legacyHidSlotAuthority.CurrentServiceGeneration !=
                            inputServiceGeneration)
                    {
                        return;
                    }

                    if (!switch2BluetoothProductionCoordinator.TryStart(
                            inputServiceGeneration, hostAddress,
                            out Switch2BluetoothWindowsScanStartFailure failure))
                    {
                        LogDebug($"Switch 2 Bluetooth discovery could not start: {failure}.", true);
                    }
                    // Once the host lookup/start call finishes, the coordinator
                    // supplies live scan/cleanup status. A late attempt cannot
                    // overwrite Stop or a newer discovery attempt.
                    switch2BluetoothDiscoveryStartupState.TryComplete(exactStartingStatus,
                        Switch2BluetoothDiscoveryState.Stopped);
                }
            }
            catch (OperationCanceledException)
            {
                switch2BluetoothDiscoveryStartupState.TryComplete(exactStartingStatus,
                    Switch2BluetoothDiscoveryState.Stopped);
            }
            catch (Exception exception)
            {
                switch2BluetoothDiscoveryStartupState.TryComplete(exactStartingStatus,
                    Switch2BluetoothDiscoveryState.StartFailed);
                StartupDiag($"Switch 2 Bluetooth discovery startup failed: {exception.GetType().Name}");
                LogDebug("Switch 2 Bluetooth discovery could not start.", true);
            }
        }

        private void OnSwitch2RuntimeAttached(
            InputControllerSlotToken token)
        {
            if (!token.IsValid || token.Registration.Device is not
                    Switch2RuntimeInputDevice device ||
                !ReferenceEquals(DS4Controllers[token.Slot], device))
            {
                return;
            }

            using (ReadLocker locker = new ReadLocker(
                       slotManager.CollectionLocker))
            {
                activeControllers = slotManager.ControllerColl.Count;
            }
            HotplugController?.Invoke(this, device, token.Slot);
        }

        private void OnSwitch2RuntimeRemoved(InputControllerSlotToken token)
        {
            if (!token.IsValid || token.Registration.Device is not
                    Switch2RuntimeInputDevice device)
            {
                return;
            }
            // Do not raise the legacy DS4Device.Removal event: its subscribers
            // own the HID teardown path. This is a completed typed retirement,
            // identified by the old object even if the slot is already reused.
            Dispatcher dispatcher = eventDispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                using (ReadLocker locker = new ReadLocker(slotManager.CollectionLocker))
                {
                    activeControllers = slotManager.ControllerColl.Count;
                }
                RemovedController?.Invoke(this, device, token.Slot);
            }));
        }

        internal Switch2BluetoothDiscoveryStatus GetSwitch2BluetoothDiscoveryStatus()
        {
            if (!Volatile.Read(ref running))
                return Switch2BluetoothDiscoveryStatus.Stopped;
            Switch2BluetoothDiscoveryStatus startup =
                switch2BluetoothDiscoveryStartupState.Snapshot;
            return startup.State == Switch2BluetoothDiscoveryState.Stopped ?
                switch2BluetoothProductionCoordinator.GetDiscoveryStatus() : startup;
        }

        internal Switch2BluetoothAssociationCandidate[]
            GetSwitch2BluetoothAssociationCandidates() =>
            switch2BluetoothProductionCoordinator.GetAssociationCandidates();

        internal ValueTask<Switch2BluetoothWindowsAssociationResult>
            AssociateSwitch2BluetoothAsync(int candidateId,
                CancellationToken cancellationToken = default) =>
            switch2BluetoothProductionCoordinator.AssociateAsync(candidateId,
                cancellationToken);

        internal Switch2JoyConPairCandidate[]
            GetSwitch2JoyConPairCandidates() =>
            switch2BluetoothProductionCoordinator.GetJoyConPairCandidates();

        internal ValueTask<Switch2JoyConPairActivationResult>
            CreateAndActivateSwitch2JoyConPairAsync(int leftCandidateId,
                int rightCandidateId,
                CancellationToken cancellationToken = default) =>
            switch2BluetoothProductionCoordinator.
                CreateAndActivateJoyConPairAsync(leftCandidateId,
                    rightCandidateId, cancellationToken);

        internal ValueTask<Switch2JoyConStandaloneActivationResult>
            ActivateSwitch2JoyConSeparatelyAsync(int candidateId,
                CancellationToken cancellationToken = default) =>
            switch2BluetoothProductionCoordinator.
                ActivateJoyConSeparatelyAsync(candidateId,
                    cancellationToken);

        internal ValueTask<int> ReconcileAutomaticSwitch2JoyConPairsAsync(
            CancellationToken cancellationToken = default) =>
            switch2BluetoothProductionCoordinator.
                ReconcileAutomaticJoyConPairsAsync(cancellationToken);

        private bool StartCore(bool showlog)
        {
            hidHideManagedDevices.OpenLifecycle();
            reportDiagnosticsWorker.Resume();
            StartupDiag($"ControlService.Start enter showlog={showlog} running={running} inServiceTask={inServiceTask} admin={Global.IsAdministrator()}");
            inServiceTask = true;
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
                LogDebug("VIIPER virtual-controller backend ready");

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

                if (!legacyHidSlotAuthority.TryOpenNext(
                        out ulong inputServiceGeneration,
                        out ControlServiceLegacyHidSlotFailure slotFailure,
                        out InputControllerSlotTableFailure tableFailure))
                {
                    throw new InvalidOperationException(
                        $"Input slot authority could not open: {slotFailure}/{tableFailure}.");
                }
                StartupDiag($"Input slot authority opened generation={inputServiceGeneration}");
                if (!switch2RuntimeRegistrationService.TryAdoptOpen(
                        inputServiceGeneration,
                        out Switch2RuntimeRegistrationTransactionFailure
                            switch2OpenFailure))
                {
                    legacyHidSlotAuthority.TryClose(out _, out _, out _);
                    throw new InvalidOperationException(
                        $"Switch 2 input registration could not adopt the shared generation: {switch2OpenFailure.Kind}/{switch2OpenFailure.TableFailure}.");
                }
                StartupDiag($"Switch 2 input registration adopted shared generation={inputServiceGeneration}");

                try
                {
                    loopControllers = true;
                    StartupDiag("AssignInitialDevices begin");
                    AssignInitialDevices();
                    StartupDiag("AssignInitialDevices end");

                    // A force-closed prior development build can leave its
                    // USB/IP output imported. Remove those ports before HID
                    // discovery or DS4Windows will ingest its own VIIPER DS4,
                    // create a second output/UAC endpoint, and recurse.
                    ViiperUsbipPortManager.RecoverStaleLocalViiperPortsAtStartup();
                    // Let usbccgp/HID finish publishing removal before the
                    // first input snapshot; otherwise a detached interface can
                    // remain enumerable for one final discovery pass.
                    Thread.Sleep(250);

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
                        while (i < CURRENT_DS4_CONTROLLER_LIMIT &&
                            !inputSlotAdmission.TryClaimLegacySlot(i, device)) i++;
                        if (i >= CURRENT_DS4_CONTROLLER_LIMIT) break;
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
                                        currentJoyDev.TryDetachJointDevice(parentJoy);
                                    };
                                    currentJoyDev.Removal += (sender, args) =>
                                    {
                                        parentJoy.TryDetachJointDevice(currentJoyDev);
                                    };

                                    tempPrimaryJoyDev = null;
                                }
                            }
                        }

                        StartupDiag($"PrepareConnectedInputControllerSettingEvents begin index={i}");
                        PrepareConnectedInputControllerAtSlot(numControllers,
                            device, index: i);
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
                StartGameBarStateTimer();
                if (!switch2ProUsbProductionCoordinator.TryStart(
                        inputServiceGeneration))
                {
                    LogDebug(
                        "Switch 2 Pro USB discovery could not start.", true);
                }
                BeginSwitch2BluetoothDiscovery(inputServiceGeneration);

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
            inServiceTask = false;
            runHotPlug = true;
            StartupDiag("ControlService.Start before ServiceStarted events");
            ServiceStarted?.Invoke(this, EventArgs.Empty);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            StartupDiag("ControlService.Start after RunningChanged");
            ProcessPriorityClass appliedPriority =
                ManagedAudioLatencyLease.ApplyRequestedProcessPriority(
                    MainWindow.ProcessPriorityClasses[Global.ProcessPriority]);
            StartupDiag($"ControlService.Start exit priority={appliedPriority}");
            return true;
        }

        private void PrepareDevUDPMotion(DS4Device device, int index)
        {
            DS4Device.ReportHandler<EventArgs> motionHandler =
                CreateDevUDPMotionHandler(index, device);

            if (legacyHidSlotAuthority.TryGetExactBinding(index, device,
                    out ControlServiceLegacyHidSlotBinding binding))
            {
                DS4Device.ReportHandler<EventArgs> admittedHandler =
                    (sender, args) =>
                    {
                        if (!legacyHidSlotAuthority.TryAcquireReport(binding,
                                sender, out InputControllerReportLease lease,
                                out _))
                        {
                            return;
                        }
                        using (lease)
                        {
                            motionHandler(sender, args);
                        }
                    };
                if (!legacyHidSlotAuthority.TryReplaceMotionHandler(binding,
                        admittedHandler, subscribe: true, out var failure))
                {
                    throw new InvalidOperationException(
                        $"Exact UDP motion hook failed: {failure}.");
                }
                return;
            }

            device.MotionEvent = motionHandler;
            device.Report += motionHandler;
        }

        private DS4Device.ReportHandler<EventArgs>
            CreateDevUDPMotionHandler(int index, DS4Device sourceDevice)
        {
            int tempIdx = index;
            // UDP filtering/yaw policy is an observation consumer, not a
            // producer of mapper motion. Keep its mutable scratch independent
            // of both CurrentState.Motion and the mapper's TempState buffer.
            var udpObservation = new DS4StateOwnedSnapshot();
            return (sender, args) =>
            {
                if (!ReferenceEquals(sender, sourceDevice) ||
                    !ReferenceEquals(DS4Controllers[tempIdx], sourceDevice))
                    return;
                DualShockPadMeta padDetail = new DualShockPadMeta();
                GetPadDetailForIdx(tempIdx, ref padDetail);
                udpObservation.Capture(CurrentState[tempIdx]);
                DS4State stateForUdp = udpObservation.State;

                if (Global.IsUsingUDPServerSmoothing() && stateForUdp.Motion != null)
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

                if (sourceDevice is Switch2RuntimeInputDevice &&
                    stateForUdp.Motion != null)
                {
                    stateForUdp.Motion.angVelYaw =
                        Switch2CemuhookYawPolicy.ApplyYaw(
                            stateForUdp.Motion.angVelYaw,
                            Global.Switch2CemuhookYawSensitivity[tempIdx]);
                }

                _udpServer?.NewReportIncoming(ref padDetail, stateForUdp, udpOutBuffers[tempIdx]);
            };
        }

        private void RemoveDevUDPMotion(DS4Device device)
        {
            int slot = device?.DeviceSlotNumber ?? -1;
            if (legacyHidSlotAuthority.TryGetExactBinding(slot, device,
                    out ControlServiceLegacyHidSlotBinding binding))
            {
                legacyHidSlotAuthority.TryReplaceMotionHandler(binding, null,
                    subscribe: false, out _);
                return;
            }
            if (device?.MotionEvent != null)
            {
                device.Report -= device.MotionEvent;
                device.MotionEvent = null;
            }
        }

        private void SetDevUDPMotionSubscription(DS4Device device,
            bool subscribe)
        {
            int slot = device?.DeviceSlotNumber ?? -1;
            if (legacyHidSlotAuthority.TryGetExactBinding(slot, device,
                    out ControlServiceLegacyHidSlotBinding binding))
            {
                legacyHidSlotAuthority.TrySetMotionSubscription(binding,
                    subscribe, out _);
                return;
            }
            if (device?.MotionEvent == null)
            {
                return;
            }
            if (subscribe)
            {
                device.Report += device.MotionEvent;
            }
            else
            {
                device.Report -= device.MotionEvent;
            }
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

        public bool Stop(bool showlog = true, bool immediateUnplug = false)
        {
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
            lock (serviceLifecycleLock)
            {
                return StopCore(showlog, immediateUnplug);
            }
        }

        private bool StopCore(bool showlog, bool immediateUnplug)
        {
            StartupDiag($"ControlService.Stop enter showlog={showlog} immediate={immediateUnplug} running={running}");
            // Reject HotPlug claims immediately. The generation check inside
            // Ensure prevents work that entered before running became false
            // from mutating HidHide after service-wide cleanup starts.
            hidHideManagedDevices.CloseLifecycle();
            bool resumingExactTypedStop = !running &&
                exactTypedStopRetryPending;
            if (running || resumingExactTypedStop)
            {
                if (!resumingExactTypedStop)
                {
                    bool switch2UsbStopped =
                        switch2ProUsbProductionCoordinator.StopAsync().
                            AsTask().GetAwaiter().GetResult();
                    if (!switch2UsbStopped)
                    {
                        StartupDiag("ControlService.Stop Switch 2 Pro USB " +
                            "runtime did not quiesce");
                        hidHideManagedDevices.OpenLifecycle();
                        return false;
                    }
                    switch2BluetoothDiscoveryStartupState.Set(Switch2BluetoothDiscoveryState.Stopping);
                    switch2BluetoothStartupCancellation?.Cancel();
                    bool switch2DiscoveryStopped =
                        switch2BluetoothProductionCoordinator.StopAsync().
                            AsTask().GetAwaiter().GetResult();
                    switch2BluetoothStartupCancellation?.Dispose();
                    switch2BluetoothStartupCancellation = null;
                    // A false bounded Stop can still be draining. Hand status
                    // back to the exact coordinator task instead of freezing a
                    // timeout as a permanent failure in the Settings UI.
                    switch2BluetoothDiscoveryStartupState.Set(Switch2BluetoothDiscoveryState.Stopped);
                    if (!switch2DiscoveryStopped)
                    {
                        StartupDiag("ControlService.Stop Switch 2 Bluetooth discovery did not quiesce");
                        hidHideManagedDevices.OpenLifecycle();
                        return false;
                    }

                    ulong inputServiceGeneration =
                        legacyHidSlotAuthority.CurrentServiceGeneration;
                    InputControllerSlotSnapshot[] inputSnapshots =
                        Array.Empty<InputControllerSlotSnapshot>();
                    if (inputServiceGeneration != 0 &&
                        !legacyHidSlotAuthority.TryClose(
                            out inputSnapshots,
                            out ControlServiceLegacyHidSlotFailure closeFailure,
                            out InputControllerSlotTableFailure tableFailure))
                    {
                        StartupDiag($"ControlService.Stop input slot close rejected: {closeFailure}/{tableFailure}");
                        hidHideManagedDevices.OpenLifecycle();
                        return false;
                    }
                    if (inputServiceGeneration != 0)
                    {
                        if (!switch2RuntimeRegistrationService.
                                TryObserveExternalTableClose(
                                    inputServiceGeneration, inputSnapshots,
                                    out Switch2RuntimeRegistrationTransactionFailure
                                        switch2ObserveFailure))
                        {
                            throw new InvalidOperationException(
                                $"Switch 2 input registration rejected the shared close snapshot: {switch2ObserveFailure.Kind}/{switch2ObserveFailure.TableFailure}.");
                        }
                        inputRegistrationCloseGeneration =
                            inputServiceGeneration;
                    }
                    if (OpenRGBServer.Instance.IsRunning)
                    {
                        StartupDiag("ControlService.Stop OpenRGB stop begin");
                        OpenRGBServer.Instance.Stop();
                        StartupDiag("ControlService.Stop OpenRGB stop end");
                    }

                    running = false;
                    reportDiagnosticsWorker.Pause();
                    runHotPlug = false;
                    inServiceTask = true;
                    StopGameBarStateTimer();
                    StopAllGameBarCompatibilityOutputs();
                    StartupDiag("ControlService.Stop PreServiceStop begin");
                    PreServiceStop?.Invoke(this, EventArgs.Empty);
                    StartupDiag("ControlService.Stop PreServiceStop end");

                    if (showlog)
                        LogDebug(DS4WinWPF.Properties.Resources.StoppingX360);

                    LogDebug("Closing VIIPER virtual-controller connections");
                }
                else
                {
                    inServiceTask = true;
                    StartupDiag("ControlService.Stop resuming exact typed legacy retirement without replaying stop preamble");
                }

                if (inputRegistrationCloseGeneration != 0)
                {
                    if (!switch2RuntimeRegistrationService.TryClose(
                            inputRegistrationCloseGeneration, 5_000,
                            out Switch2RuntimeRegistrationTransactionFailure
                                switch2CloseFailure))
                    {
                        StartupDiag($"ControlService.Stop Switch 2 registration close requires retry: {switch2CloseFailure.Kind}/{switch2CloseFailure.TableFailure}");
                        exactTypedStopRetryPending = true;
                        inServiceTask = false;
                        return false;
                    }
                    inputRegistrationCloseGeneration = 0;
                }

                bool anyUnplugged = false;
                bool typedLegacyStopFailed = false;
                for (int i = 0, arlength = DS4Controllers.Length; i < arlength; i++)
                {
                    DS4Device tempDevice = DS4Controllers[i];
                    if (tempDevice != null)
                    {
                        StartupDiag($"ControlService.Stop controller loop index={i} display={tempDevice.DisplayName} mac={tempDevice.MacAddress} conn={tempDevice.ConnectionType} charging={tempDevice.isCharging()}");
                        if (!TryRetireMouseCallbacks(i, tempDevice))
                        {
                            typedLegacyStopFailed = true;
                            break;
                        }
                        if (legacyHidSlotAuthority.TryGetExactBinding(i,
                                tempDevice,
                                out ControlServiceLegacyHidSlotBinding
                                    typedBinding))
                        {
                            bool hadOutput = outputDevices[i] != null;
                            if (!RetireTypedLegacyControllerForServiceStop(
                                    typedBinding, immediateUnplug))
                            {
                                typedLegacyStopFailed = true;
                                break;
                            }
                            anyUnplugged |= hadOutput;
                            continue;
                        }
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
                        oscState[i] = new DS4State();
                        touchPad[i] = null;
                        lag[i] = false;
                        inWarnMonitor[i] = false;
                        inputSlotAdmission.TryReleaseLegacySlot(i, tempDevice);
                    }
                }

                if (typedLegacyStopFailed)
                {
                    StartupDiag("ControlService.Stop fail-closed because exact typed legacy retirement was not proven");
                    exactTypedStopRetryPending = true;
                    inServiceTask = false;
                    return false;
                }
                exactTypedStopRetryPending = false;

                if (showlog)
                    LogDebug(DS4WinWPF.Properties.Resources.StoppingDS4);

                StartupDiag("ControlService.Stop DualSenseAudio reset begin");
                dualSenseAudioPassthrough.ResetForServiceStop();
                StartupDiag("ControlService.Stop DualSenseAudio reset end");
                StartupDiag("ControlService.Stop DualShock4Audio reset begin");
                dualShock4AudioPassthrough.ResetForServiceStop();
                StartupDiag("ControlService.Stop DualShock4Audio reset end");
                StartupDiag("ControlService.Stop DualSenseMicrophone stop begin");
                dualSenseMicrophonePassthrough.Stop();
                StartupDiag("ControlService.Stop DualSenseMicrophone stop end");
                StartupDiag("ControlService.Stop AudioHaptics reset begin");
                audioHapticsService.ResetForServiceStop();
                StartupDiag("ControlService.Stop AudioHaptics reset end");
                StartupDiag("ControlService.Stop PlayStation feature outputs begin");
                StopAllPlayStationFeatureOutputs();
                StartupDiag("ControlService.Stop PlayStation feature outputs end");
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

                // Disconnect from KBM system when stopping ControlService
                StartupDiag($"ControlService.Stop outputKBM Disconnect begin handler={outputKBMHandler?.GetFullDisplayName()}");
                LogDebug($"Closing connection to output handler {outputKBMHandler.GetDisplayName()}");
                outputKBMHandler.Disconnect();
                StartupDiag("ControlService.Stop outputKBM Disconnect end");
                inServiceTask = false;
                activeControllers = 0;
            }

            runHotPlug = false;
            // Release only entries for controllers managed by this service run after all
            // controller handles are closed. Unrelated HidHide entries remain untouched.
            // Start will reacquire hiding as each managed controller is discovered again.
            ReleaseHidHideManagedDevices();
            StartupDiag("ControlService.Stop before stopped events");
            ServiceStopped?.Invoke(this, EventArgs.Empty);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            StartupDiag("ControlService.Stop exit");
            return true;
        }

        public bool HotPlug()
        {
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback) return false;
            lock (serviceLifecycleLock)
            {
                return HotPlugCore();
            }
        }

        private bool HotPlugCore()
        {
            if (running)
            {
                inServiceTask = true;
                loopControllers = true;
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
                        if (inputSlotAdmission.TryClaimLegacySlot(Index, device))
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
                                            currentJoyDev.TryDetachJointDevice(secondaryJoy);
                                        };
                                        currentJoyDev.Removal += (sender, args) =>
                                        {
                                            secondaryJoy.TryDetachJointDevice(currentJoyDev);
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
                                            currentJoyDev.TryDetachJointDevice(parentJoy);
                                        };
                                        currentJoyDev.Removal += (sender, args) =>
                                        {
                                            parentJoy.TryDetachJointDevice(currentJoyDev);
                                        };

                                        tempPrimaryJoyDev = null;
                                    }
                                }
                            }

                            PrepareConnectedInputControllerAtSlot(
                                numControllers, device, Index);

                            HotplugController?.Invoke(this, device, Index);
                            break;
                        }
                    }
                }

                inServiceTask = false;
            }

            return true;
        }

        private void PrepareConnectedInputControllerAtSlot(int numControllers,
            DS4Device device, int index)
        {
            if (device.WorkerLifecycleSupport !=
                DS4DeviceWorkerLifecycleSupport.SupportedLegacyHid)
            {
                device.DeviceSlotNumber = index;
                PrepareConnectedInputControllerSettingEvents(numControllers,
                    device, index);
                return;
            }

            bool hasPersistentIdentity = device.AllowsPersistentIdentity &&
                device.isValidSerial();
            if (!legacyHidSlotAuthority.TryBindExactSlot(index, device,
                    hasPersistentIdentity,
                    out ControlServiceLegacyHidSlotBinding binding,
                    out ControlServiceLegacyHidSlotFailure slotFailure,
                    out InputControllerSlotTableFailure tableFailure))
            {
                throw new InvalidOperationException(
                    $"Typed legacy slot bind failed at {index}: {slotFailure}/{tableFailure}.");
            }

            device.DeviceSlotNumber = index;
            try
            {
                PrepareTypedLegacyInputControllerSettingEvents(numControllers,
                    binding);
            }
            catch
            {
                // Shared profile/output staging still contains legacy hooks
                // without exact inverse tokens. Any exception after binding is
                // therefore quarantined; it is never mislabeled as a clean
                // rollback or made reusable by clearing only the array slot.
                legacyHidSlotAuthority.TryQuarantinePrepared(binding, out _,
                    out _);
                throw;
            }
        }

        private void PrepareConnectedInputControllerSettingEvents(
            int numControllers, DS4Device device, int index)
        {
            StartupDiag($"Controller prep begin index={index} numControllers={numControllers} display={device.DisplayName} mac={device.MacAddress} type={device.DeviceType}");
            PrepareConnectedInputControllerSettingsBeforeLifecycle(
                numControllers, device, index);

            ReportDiagnosticsWorker.Source diagnosticsSource =
                reportDiagnosticsWorker.Register(index, device);
            device.Removal += (sender, e) =>
            {
                diagnosticsSource?.Retire();
                On_DS4Removal(sender, e);
            };
            device.Removal += DS4Devices.On_Removal;
            device.SyncChange += this.On_SyncChange;
            device.SyncChange += DS4Devices.UpdateSerial;
            device.SerialChange += this.On_SerialChange;
            device.ChargingChanged += CheckQuickCharge;

            PrepareConnectedInputControllerProfileMappingOutput(device,
                index);

            int tempIdx = index;
            device.Report += (sender, e) =>
            {
                this.On_Report(sender, e, tempIdx, diagnosticsSource);
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
            QueueSteamInputReclaim(device);
            StartupDiag($"device.StartUpdate end index={index}");
            StartupDiag($"Controller prep end index={index}");
        }

        private void PrepareTypedLegacyInputControllerSettingEvents(
            int numControllers, ControlServiceLegacyHidSlotBinding binding)
        {
            DS4Device device = binding.Device;
            int index = binding.Slot;
            StartupDiag($"Typed controller prep begin index={index} generation={binding.ConnectionGeneration} display={device.DisplayName}");
            PrepareConnectedInputControllerSettingsBeforeLifecycle(
                numControllers, device, index);

            ReportDiagnosticsWorker.Source diagnosticsSource =
                reportDiagnosticsWorker.Register(index, device);
            binding.DiagnosticsSource = diagnosticsSource;
            EventHandler<EventArgs> removalHandler = (sender, e) =>
            {
                if (!ReferenceEquals(sender, binding.Device) ||
                    !legacyHidSlotAuthority.TryClaimRemovalQueue(binding))
                {
                    return;
                }
                diagnosticsSource?.Retire();
                Task.Run(() => RetireTypedLegacyController(binding));
            };
            EventHandler<EventArgs> syncHandler = this.On_SyncChange;
            EventHandler<EventArgs> registrySyncHandler =
                DS4Devices.UpdateSerial;
            EventHandler<EventArgs> serialHandler = this.On_SerialChange;
            EventHandler chargingHandler = CheckQuickCharge;
            if (!legacyHidSlotAuthority.TrySubscribeLegacyLifecycle(binding,
                    removalHandler, syncHandler, registrySyncHandler,
                    serialHandler, chargingHandler,
                    out ControlServiceLegacyHidSlotFailure lifecycleFailure))
            {
                throw new InvalidOperationException(
                    $"Typed legacy lifecycle hooks failed: {lifecycleFailure}.");
            }

            PrepareConnectedInputControllerProfileMappingOutput(device,
                index);

            DS4Device.ReportHandler<EventArgs> reportHandler = (sender, e) =>
            {
                if (!legacyHidSlotAuthority.TryAcquireReport(binding, sender,
                        out InputControllerReportLease lease, out _))
                {
                    return;
                }
                using (lease)
                {
                    On_Report(sender, e, index, diagnosticsSource);
                }
            };
            if (!legacyHidSlotAuthority.TrySubscribeReport(binding,
                    reportHandler, out ControlServiceLegacyHidSlotFailure
                        reportFailure))
            {
                throw new InvalidOperationException(
                    $"Typed legacy report hook failed: {reportFailure}.");
            }
            StartupDiag($"Typed report hook added index={index}");

            if (_udpServer != null && index < UdpServer.NUMBER_SLOTS)
            {
                StartupDiag($"PrepareDevUDPMotion begin index={index}");
                PrepareDevUDPMotion(device, index);
                StartupDiag($"PrepareDevUDPMotion end index={index}");
            }

            StartupDiag($"typed device.StartUpdate begin index={index}");
            if (!legacyHidSlotAuthority.TryStartAndActivate(binding,
                    out ControlServiceLegacyHidSlotFailure startFailure,
                    out InputControllerSlotTableFailure tableFailure,
                    out DS4DeviceWorkerLifecycleResult workerResult))
            {
                throw new InvalidOperationException(
                    $"Typed legacy worker start failed: {startFailure}/{tableFailure}/{workerResult.FailureKind}.");
            }
            QueueSteamInputReclaim(device);
            StartupDiag($"typed device.StartUpdate end index={index}");
            StartupDiag($"Typed controller prep end index={index}");
        }

        private void PrepareConnectedInputControllerSettingsBeforeLifecycle(
            int numControllers, DS4Device device, int index)
        {
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
        }

        private void PrepareConnectedInputControllerProfileMappingOutput(
            DS4Device device, int index)
        {
            StartupDiag($"TouchPad create begin index={index}");
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback)
                throw new InvalidOperationException("Mouse callback replacement requires a cold lifecycle boundary.");
            lock (serviceLifecycleLock)
            {
                Mouse previousMouse = touchPad[index];
                if (!mouseCallbackRegistry.TryRetireMouse(index, previousMouse,
                        MouseCallbackRetirementTimeoutMilliseconds))
                {
                    WarnMouseCallbackRetirement(index);
                    throw new InvalidOperationException("The previous Mouse callback lifetime has not drained.");
                }
                touchPad[index] = new Mouse(index, device);
            }
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

                int outputPeerSlot = device.JointDeviceSlotNumber;
                if (!getDInputOnly(index) && device.isSynced())
                {
                    if (device.PrimaryDevice)
                    {
                        StartupDiag($"PluginOutDev begin index={index} outType={Global.OutContType[index]}");
                        PluginOutDev(index, device);
                        StartupDiag($"PluginOutDev end index={index} useDInputOnly={useDInputOnly[index]} activeOut={activeOutDevType[index]} outDev={outputDevices[index]?.GetDeviceType() ?? "null"}");
                    }
                    else if ((uint)outputPeerSlot < (uint)outputDevices.Length)
                    {
                        int otherIdx = outputPeerSlot;
                        OutputDevice tempOutDev = outputDevices[otherIdx];
                        if (tempOutDev != null)
                        {
                            OutContType tempConType = activeOutDevType[otherIdx];
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

                int gyroPeerSlot = device.JointDeviceSlotNumber;
                if (device.PrimaryDevice && device.OutputMapGyro)
                {
                    StartupDiag($"TouchPadOn begin index={index}");
                    TouchPadOn(index, device);
                    StartupDiag($"TouchPadOn end index={index}");
                }
                else if ((uint)gyroPeerSlot < (uint)DS4Controllers.Length)
                {
                    int otherIdx = gyroPeerSlot;
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

        /// <summary>
        /// Returns the VIIPER device that owns the Windows PlayStation audio
        /// endpoints for a physical controller. PlayStation personas use their
        /// game-visible composite device; Xbox and Switch personas use a
        /// persistent audio-only companion.
        /// </summary>
        internal ViiperOutDevice GetPlayStationFeatureOutput(int index)
        {
            if (index < 0 || index >= MAX_DS4_CONTROLLER_COUNT)
            {
                return null;
            }

            ViiperOutDevice primary = outputDevices[index] as ViiperOutDevice;
            if (primary != null &&
                PlayStationFeatureOutputPolicy.IsPlayStationAudioOutput(
                    primary.OutputType))
            {
                return primary;
            }

            lock (playStationFeatureOutputLock)
            {
                return playStationFeatureOutputDevices[index];
            }
        }

        internal OutContType GetPlayStationFeatureOutputType(int index)
        {
            return GetPlayStationFeatureOutput(index)?.OutputType ??
                OutContType.None;
        }

        private ViiperOutDevice EnsurePlayStationFeatureOutput(
            int index, DS4Device source)
        {
            ViiperOutDevice primary = outputDevices[index] as ViiperOutDevice;
            OutContType primaryType = primary?.OutputType ??
                Global.OutContType[index].Normalize();

            if (primary?.IsRuntimeConnected == true &&
                PlayStationFeatureOutputPolicy.IsPlayStationAudioOutput(
                    primaryType))
            {
                DisconnectPlayStationFeatureOutput(index);
                return primary;
            }

            OutContType desiredSidecar = primary?.IsRuntimeConnected == true
                ? PlayStationFeatureOutputPolicy.GetAudioOnlySidecarType(
                    source, primaryType, getDInputOnly(index))
                : OutContType.None;
            if (desiredSidecar == OutContType.None)
            {
                DisconnectPlayStationFeatureOutput(index);
                return null;
            }

            lock (playStationFeatureOutputLock)
            {
                ViiperOutDevice existing =
                    playStationFeatureOutputDevices[index];
                if (existing?.IsRuntimeConnected == true &&
                    existing.OutputType == desiredSidecar)
                {
                    existing.BindPhysicalController(index);
                    return existing;
                }

                if (existing != null)
                {
                    playStationFeatureOutputDevices[index] = null;
                    existing.Disconnect();
                }

                ViiperOutDevice sidecar = new ViiperOutDevice(
                    desiredSidecar,
                    PlayStationFeatureOutputPolicy.GetViiperType(
                        desiredSidecar),
                    audioOnlySidecar: true);
                try
                {
                    StartupDiag(
                        $"Persistent PlayStation audio owner connect begin index={index} type={desiredSidecar}");
                    sidecar.Connect();
                    sidecar.BindPhysicalController(index);
                    playStationFeatureOutputDevices[index] = sidecar;
                    StartupDiag(
                        $"Persistent PlayStation audio owner ready index={index} type={desiredSidecar} port={sidecar.DirectSpeakerUsbipPort}");
                    return sidecar;
                }
                catch (Exception ex)
                {
                    sidecar.Disconnect();
                    AppLogger.LogToGui(
                        $"Could not create the {desiredSidecar.ToDisplayName()} audio interface for controller #{index + 1}: {ex.Message}",
                        true);
                    StartupDiag(
                        $"PlayStation audio sidecar failed index={index} type={desiredSidecar} {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        private void DisconnectPlayStationFeatureOutput(int index)
        {
            ViiperOutDevice sidecar = null;
            lock (playStationFeatureOutputLock)
            {
                if (index >= 0 && index <
                    playStationFeatureOutputDevices.Length)
                {
                    sidecar = playStationFeatureOutputDevices[index];
                    playStationFeatureOutputDevices[index] = null;
                }
            }

            if (sidecar != null)
            {
                StartupDiag(
                    $"Persistent PlayStation audio owner disconnect index={index} type={sidecar.OutputType}");
                sidecar.Disconnect();
            }
        }

        private void StopAllPlayStationFeatureOutputs()
        {
            for (int index = 0; index <
                playStationFeatureOutputDevices.Length; index++)
            {
                DisconnectPlayStationFeatureOutput(index);
            }
        }

        public void CheckProfileOptions(int ind, DS4Device device, bool startUp = false)
        {
            EnsureVirtualMouseForStickMouseProfile(ind);

            ViiperOutDevice playStationFeatureOutput =
                EnsurePlayStationFeatureOutput(ind, device);
            OutContType playStationFeatureOutputType =
                playStationFeatureOutput?.OutputType ?? OutContType.None;

            if (device.DeviceType == InputDevices.InputDeviceType.DS4)
                device.ConfigureDualShock4ProfileOutput(getEnableOutputDataToDS4(ind));
            else
                device.ModifyFeatureSetFlag(VidPidFeatureSet.NoOutputData, !getEnableOutputDataToDS4(ind));
            if (device is Switch2RuntimeInputDevice)
            {
                (outputDevices[ind] as ViiperOutDevice)?.
                    QueueSwitch2DualSenseConversionPolicyRefresh(ind);
            }
            (outputDevices[ind] as ViiperOutDevice)?.
                QueueXboxFeedbackPolicyRefresh(ind);
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

            TriggerLabProfileSettings triggerLab = Global.store.triggerLabSettings[ind].Normalize();
            if (device is InputDevices.DualSenseDevice triggerLabDevice && triggerLab.HasActiveOverride)
            {
                TriggerLabEffectEncoder.ApplyToDevice(triggerLabDevice,
                    InputDevices.TriggerId.LeftTrigger, triggerLab.Left,
                    triggerLab.LeftActive);
                TriggerLabEffectEncoder.ApplyToDevice(triggerLabDevice,
                    InputDevices.TriggerId.RightTrigger, triggerLab.Right,
                    triggerLab.RightActive);
            }
            else
            {
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[ind].TriggerEffect,
                    Global.L2OutputSettings[ind].TrigEffectSettings);
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[ind].TriggerEffect,
                    Global.R2OutputSettings[ind].TrigEffectSettings);
            }

            device.RumbleAutostopTime = getRumbleAutostopTime(ind);
            device.setRumble(0, 0);
            device.LightBarColor = Global.getMainColor(ind);

            // DualSense specific profile settings
            if (device is InputDevices.DualSenseDevice dualsense)
            {
                dualShock4AudioPassthrough.Stop(ind);
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
                bool speakerEnabled = IsControllerSpeakerEnabled(ind);
                DualSenseMuteButtonRuntimePolicy muteButtonPolicy =
                    ResolveDualSenseMuteButtonPolicy(ind);
                bool audioHapticsEnabled =
                    Global.store.audioHapticsSettings[ind]?.Enabled == true;
                bool silentHapticsCarrier =
                    RequiresDualSenseBluetoothMediaCarrier(
                        dualsense.ConnectionType, speakerEnabled,
                        audioHapticsEnabled, playStationFeatureOutputType);
                bool mediaCarrierEnabled = speakerEnabled ||
                    silentHapticsCarrier;
                string speakerCaptureEndpointId =
                    GetControllerSpeakerCaptureEndpointId(ind);
                bool headsetOnlyAudio = speakerEnabled &&
                    IsControllerHeadsetOnlyAudio(ind);
                DualSenseSpeakerTransportState speakerTransportState =
                    DualSenseSpeakerTransportState.Resolve(
                        speakerEnabled, headsetOnlyAudio,
                        DualSenseSpeakerVolume[ind],
                        dualSenseMuteLedOn[ind], muteButtonPolicy);
                byte activeHeadphoneVolume = speakerEnabled ?
                    DualSenseHeadphoneVolume[ind] : (byte)0;
                bool headsetOutputRouteChanged =
                    dualsense.HeadsetOnlyAudio != headsetOnlyAudio;
                bool muteLatched = dualSenseMuteLedOn[ind];
                bool speakerMuteOverride = muteButtonPolicy.
                    CanMuteBuiltInSpeaker(speakerEnabled,
                        headsetOnlyAudio);
                // Publish the carrier gate, route, both gains, and every mute
                // override as one immutable compositor state. In particular,
                // a reconnect into an already-muted slot can never expose the
                // default speaker gain between enabling the carrier and
                // applying the zero-gain mute.
                dualsense.SetProfileAudioAndMuteButtonState(
                    mediaCarrierEnabled,
                    speakerTransportState.PhysicalSpeakerVolume,
                    activeHeadphoneVolume,
                    headsetOnlyAudio,
                    muteButtonPolicy.OverridesMuteLed,
                    muteLatched,
                    muteButtonPolicy.MutesMicrophone,
                    muteButtonPolicy.MutesMicrophone && muteLatched,
                    speakerMuteOverride,
                    speakerMuteOverride && muteLatched);
                // CheckProfileOptions can run outside the input callback. Its
                // compound profile snapshot may race a newer mute edge, so it
                // must invalidate the input-side publication cache after the
                // mailbox write. The next report then republishes the
                // authoritative latch instead of trusting a stale signature.
                InvalidateDualSenseMuteOutputSignature(
                    ref dualSenseMuteOutputSignatures[ind]);
                bool useViiperControllerMicrophone =
                    ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                        DualSenseEnableMicrophonePassthrough[ind], dualsense,
                        playStationFeatureOutputType,
                        playStationFeatureOutput);
                // The profile volume is applied once in the shared software
                // microphone processor. Request the top of the profile range;
                // DualSenseDevice maps it to the controller's 0x40 ADC ceiling
                // at the physical protocol boundary.
                dualsense.MicrophoneVolume = useViiperControllerMicrophone ?
                    byte.MaxValue : DualSenseMicrophoneVolume[ind];

                if (mediaCarrierEnabled)
                {
                    // Audio Haptics and native game haptics use the same
                    // proven continuous 0x36 media carrier as speaker audio.
                    // Turning off audible speaker streaming therefore mutes
                    // this carrier at the controller instead of disposing it.
                    // The capture/encoder remains clocked, while a zero
                    // hardware volume guarantees that no speaker or AUX audio
                    // leaks from the disabled UI setting.
                    dualSenseAudioPassthrough.Start(ind, dualsense,
                        speakerTransportState.TransportVolume,
                        (DualSenseSpeakerCompression)Global.DualSenseSpeakerCompression[ind],
                        Global.DualSenseSpeakerBassBoost[ind],
                        speakerCaptureEndpointId,
                        DualSenseAudioSpeakerEndpointId[ind],
                        playStationFeatureOutputType,
                        playStationFeatureOutput,
                        () => GetPlayStationFeatureOutput(ind));

                    // Speaker/AUX selection is an atomic state update on the
                    // active combined transport. Restarting the capture and
                    // media pacer here loses the live stream and can leave the
                    // replacement generation waiting indefinitely. Keep the
                    // existing pipeline and publish only the new route bits.
                    if (headsetOutputRouteChanged &&
                        !dualsense.RearmBluetoothHeadsetOutputRoute())
                    {
                        AppLogger.LogToGui(
                            $"DualSense audio output route update failed for controller {ind + 1}: {dualsense.LastBluetoothHapticsWriteStatus}",
                            true);
                    }
                }
                else
                {
                    dualSenseAudioPassthrough.Stop(ind);
                }

                if (DualSenseEnableMicrophonePassthrough[ind] &&
                    !useViiperControllerMicrophone)
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
                bool speakerEnabled = IsControllerSpeakerEnabled(ind);
                string speakerCaptureEndpointId =
                    GetControllerSpeakerCaptureEndpointId(ind);
                bool headsetOnlyAudio = IsControllerHeadsetOnlyAudio(ind);
                byte physicalSpeakerVolume = headsetOnlyAudio
                    ? (byte)0
                    : DualSenseSpeakerVolume[ind];
                bool useViiperControllerMicrophone =
                    ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                        DualSenseEnableMicrophonePassthrough[ind], device,
                        playStationFeatureOutputType,
                        playStationFeatureOutput);
                // VIIPER opens the physical microphone only while a Windows
                // client is actively recording. Do not arm it during profile
                // load and consume Bluetooth bandwidth before that point.
                bool microphoneEnabled =
                    ControllerMicrophoneRoutePolicy.ShouldArmPhysicalBluetoothMicrophone(
                        DualSenseEnableMicrophonePassthrough[ind], device,
                        playStationFeatureOutputType,
                        playStationFeatureOutput);
                bool audioConfigured = device.ConfigureBluetoothAudioForProfile(
                    speakerEnabled,
                    microphoneEnabled,
                    physicalSpeakerVolume,
                    DualSenseHeadphoneVolume[ind],
                    useViiperControllerMicrophone ? byte.MaxValue :
                        DualSenseMicrophoneVolume[ind]);

                if (audioConfigured && speakerEnabled)
                {
                    dualShock4AudioPassthrough.Start(ind, device,
                        physicalSpeakerVolume,
                        (DualSenseSpeakerCompression)Global.DualSenseSpeakerCompression[ind],
                        Global.DualSenseSpeakerBassBoost[ind],
                        speakerCaptureEndpointId,
                        playStationFeatureOutputType,
                        playStationFeatureOutput,
                        headsetOnlyAudio,
                        () => GetPlayStationFeatureOutput(ind));
                }
                else
                {
                    dualShock4AudioPassthrough.Stop(ind);
                }

                dualSenseMicrophonePassthrough.Stop();
            }

            audioHapticsService.Start(ind, device,
                Global.store.audioHapticsSettings[ind],
                playStationFeatureOutputType,
                DualSenseAudioSpeakerEndpointId[ind],
                playStationFeatureOutput?.DirectSpeakerUsbipPort ?? -1);

            if (!startUp)
            {
                CheckLauchProfileOption(ind, device);
            }
        }

        internal bool ApplyAudioHapticsToGameReport(int deviceIndex,
            byte[] report, int sampleOffset, int sampleLength)
        {
            return audioHapticsService.ApplyToGameHaptics(deviceIndex,
                report, sampleOffset, sampleLength);
        }

        public AudioHapticsRuntimeStatus GetAudioHapticsStatus(
            int deviceIndex)
        {
            return audioHapticsService.GetStatus(deviceIndex);
        }

        internal static bool IsAudioHapticsSpeakerOverrideActive(int index)
        {
            if (index < 0 || index >= Global.store.audioHapticsSettings.Length)
            {
                return false;
            }

            AudioHapticsProfileSettings settings =
                Global.store.audioHapticsSettings[index];
            return settings?.Enabled == true &&
                settings.Source == AudioHapticsSourceKind.AppSession &&
                settings.StreamAppAudioToController;
        }

        private static bool IsControllerSpeakerEnabled(int index) =>
            Global.DualSenseEnableSpeakerOutput[index] ||
            IsAudioHapticsSpeakerOverrideActive(index);

        internal static bool RequiresDualSenseBluetoothMediaCarrier(
            ConnectionType connectionType, bool speakerEnabled,
            bool audioHapticsEnabled, OutContType outputType)
        {
            if (connectionType != ConnectionType.BT || speakerEnabled)
            {
                return false;
            }

            outputType = outputType.Normalize();
            return audioHapticsEnabled ||
                outputType == OutContType.ViiperDualSense ||
                outputType == OutContType.ViiperDualSenseEdge;
        }

        private static bool IsControllerHeadsetOnlyAudio(int index)
        {
            if (IsAudioHapticsSpeakerOverrideActive(index))
            {
                return Global.store.audioHapticsSettings[index]
                    .StreamAppAudioToHeadsetOnly;
            }

            return Global.DualSenseHeadsetOnlyAudio[index];
        }

        private static string GetControllerSpeakerCaptureEndpointId(int index)
        {
            if (!IsAudioHapticsSpeakerOverrideActive(index))
            {
                return Global.DualSenseAudioCaptureEndpointId[index];
            }

            AudioHapticsProfileSettings settings =
                Global.store.audioHapticsSettings[index];
            if (settings?.AutomaticGameDetection == true)
            {
                return ProcessLoopbackWaveCapture
                    .BuildAutomaticEndpointId(index);
            }
            int processId = ProcessLoopbackWaveCapture.ResolveProcessId(settings);
            if (processId <= 0)
            {
                // Keep this as an explicit app endpoint instead of silently
                // falling back to system audio. The worker will remain in its
                // starting/error state until the selected app is available.
                processId = settings?.ProcessId ?? 0;
            }

            return processId > 0
                ? ProcessLoopbackWaveCapture.BuildEndpointId(processId)
                : ProcessLoopbackWaveCapture.EndpointPrefix + "unavailable";
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
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].LeftActive) return;
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.L2ModInfo[ind].MaxOutputChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                L2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(Math.Max(tempInfo.maxOutput, tempInfo.maxZone) / 100.0 * 255.0);

                // Refresh trigger effect
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].LeftActive) return;
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.L2ModInfo[ind].MaxZoneChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                L2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(Math.Max(tempInfo.maxOutput, tempInfo.maxZone) / 100.0 * 255.0);

                // Refresh trigger effect
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].LeftActive) return;
                device.PrepareTriggerEffect(InputDevices.TriggerId.LeftTrigger, Global.L2OutputSettings[tempIdx].TriggerEffect,
                    Global.L2OutputSettings[tempIdx].TrigEffectSettings);
            };

            Global.R2OutputSettings[ind].ResetEvents();
            Global.R2OutputSettings[ind].TriggerEffectChanged += (sender, e) =>
            {
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].RightActive) return;
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[tempIdx].TriggerEffect,
                    Global.R2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.R2ModInfo[ind].MaxOutputChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                R2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(tempInfo.maxOutput / 100.0 * 255.0);

                // Refresh trigger effect
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].RightActive) return;
                device.PrepareTriggerEffect(InputDevices.TriggerId.RightTrigger, Global.R2OutputSettings[tempIdx].TriggerEffect,
                    Global.R2OutputSettings[tempIdx].TrigEffectSettings);
            };
            Global.R2ModInfo[ind].MaxZoneChanged += (sender, e) =>
            {
                TriggerDeadZoneZInfo tempInfo = sender as TriggerDeadZoneZInfo;
                R2OutputSettings[tempIdx].TrigEffectSettings.maxValue = (byte)(tempInfo.maxOutput / 100.0 * 255.0);

                // Refresh trigger effect
                if (Global.store.triggerLabSettings[tempIdx].HasActiveOverride &&
                    Global.store.triggerLabSettings[tempIdx].RightActive) return;
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
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback)
                throw new InvalidOperationException("Mouse callback replacement requires a cold lifecycle boundary.");
            lock (serviceLifecycleLock)
            {
                Mouse tPad = touchPad[ind];
                if (tPad == null || !ReferenceEquals(DS4Controllers[ind], tPad.BoundDevice) ||
                    tPad.BoundDevice.IsRemoving || device.IsRemoving ||
                    (uint)device.DeviceSlotNumber >= (uint)DS4Controllers.Length ||
                    !ReferenceEquals(DS4Controllers[device.DeviceSlotNumber], device))
                    throw new InvalidOperationException("Mouse callback source or logical owner is no longer current.");
                if (!mouseCallbackRegistry.TryReplace(ind, tPad, device,
                        MouseCallbackRetirementTimeoutMilliseconds))
                {
                    WarnMouseCallbackRetirement(ind);
                    throw new InvalidOperationException("The previous Mouse callback lifetime has not drained.");
                }
                Interlocked.Exchange(ref mouseCallbackRetirementWarning[ind], 0);
            }
        }

        private bool TryRetireMouseCallbacks(int index, DS4Device source)
        {
            if (!mouseCallbackRegistry.TryRetireSource(source,
                    MouseCallbackRetirementTimeoutMilliseconds))
            {
                WarnMouseCallbackRetirement(index);
                return false;
            }
            Interlocked.Exchange(ref mouseCallbackRetirementWarning[index], 0);
            return true;
        }

        private void WarnMouseCallbackRetirement(int index)
        {
            if (Interlocked.Exchange(ref mouseCallbackRetirementWarning[index], 1) == 0)
                LogDebug($"Controller {index + 1} callback retirement has not drained; its exact slot remains unavailable for replacement.", true);
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
            if (ControlServiceMouseCallbackSubscription.IsInsideCallback)
            {
                TryClaimControllerRemoval(device);
                mouseCallbackRegistry.RevokeSourceFromCallback(device);
                return;
            }
            lock (serviceLifecycleLock)
            {
                int ind = FindExactControllerSlot(device);
                if (ind != -1 && (TryClaimControllerRemoval(device) || device.IsRemoving))
                {
                    if (!TryRetireMouseCallbacks(ind, device)) return;
                    RetireControllerPresentation(device, ind,
                        commitNeutralMapping: true);
                    if (!ClearExactControllerSlot(device, ind)) return;
                }
            }

            // The interface path may no longer resolve once PnP raises the
            // removal event.  HidHide ownership was captured while the node
            // was live, so release only this connection generation's exact
            // persistent additions after its virtual output has retired.
            ReleaseHidHideManagedDevice(device);
        }

        private void RetireTypedLegacyController(
            ControlServiceLegacyHidSlotBinding binding)
        {
            binding.DiagnosticsSource?.Retire();
            DS4Device device = binding.Device;
            lock (serviceLifecycleLock)
            {
                int index = binding.Slot;
                if (!ReferenceEquals(DS4Controllers[index], device) ||
                    !legacyHidSlotAuthority.TryGetExactBinding(index, device,
                        out ControlServiceLegacyHidSlotBinding exact) ||
                    !ReferenceEquals(exact, binding))
                {
                    return;
                }

                if (!TryClaimControllerRemoval(device) && !device.IsRemoving)
                {
                    return;
                }
                if (!TryRetireMouseCallbacks(index, device)) return;
                if (!legacyHidSlotAuthority.TryBeginRetirement(binding,
                        out ControlServiceLegacyHidSlotFailure beginFailure,
                        out InputControllerSlotTableFailure tableFailure))
                {
                    StartupDiag($"Typed legacy retirement rejected index={index}: {beginFailure}/{tableFailure}");
                    return;
                }

                if (!legacyHidSlotAuthority.TryPublishTerminalNeutral(binding,
                        () =>
                        {
                            // Drain admitted profile actions before touching
                            // their output/profile presentation. Retirement
                            // has already closed admission for new actions.
                            RetireControllerPresentation(device, index,
                                commitNeutralMapping: false, logRemoval: true);
                            CommitNeutralMapping(index);
                        },
                        InputControllerRegistration.
                            MaximumStopTimeoutMilliseconds,
                        out ControlServiceLegacyHidSlotFailure neutralFailure,
                        out tableFailure))
                {
                    StartupDiag($"Typed legacy terminal neutral failed index={index}: {neutralFailure}/{tableFailure}");
                    return;
                }
                if (!legacyHidSlotAuthority.TryFinalizeRetirement(binding,
                        InputControllerRegistration.
                            MaximumStopTimeoutMilliseconds,
                        out ControlServiceLegacyHidSlotFailure retireFailure,
                        out tableFailure))
                {
                    StartupDiag($"Typed legacy final retirement failed index={index}: {retireFailure}/{tableFailure}");
                    return;
                }
                if (!ClearExactControllerSlot(device, index)) return;
            }
            ReleaseHidHideManagedDevice(device);
        }

        private bool RetireTypedLegacyControllerForServiceStop(
            ControlServiceLegacyHidSlotBinding binding,
            bool immediateUnplug)
        {
            binding.DiagnosticsSource?.Retire();
            DS4Device device = binding.Device;
            int index = binding.Slot;
            if (!ReferenceEquals(DS4Controllers[index], device))
            {
                StartupDiag($"Typed legacy stop rejected stale slot index={index}");
                return false;
            }
            if (!TryRetireMouseCallbacks(index, device)) return false;
            if (binding.State ==
                ControlServiceLegacyHidSlotState.Quarantined)
            {
                TryClaimControllerRemoval(device);
                if (binding.RetirementClaim.IsValid &&
                    !inputRegistrationTable.TryWaitForDrain(binding.RetirementClaim,
                        InputControllerRegistration.MaximumStopTimeoutMilliseconds,
                        out var drainFailure))
                {
                    StartupDiag($"Typed legacy quarantined presentation drain failed index={index}: {drainFailure}");
                    return false;
                }
                RetireControllerPresentation(device, index,
                    commitNeutralMapping: true, logRemoval: false);
                if (!legacyHidSlotAuthority.
                        TryRecoverQuarantinedActivation(binding,
                            InputControllerRegistration.
                                MaximumStopTimeoutMilliseconds,
                            out ControlServiceLegacyHidSlotFailure
                                recoveryFailure))
                {
                    StartupDiag($"Typed legacy quarantined recovery failed index={index}: {recoveryFailure}");
                    return false;
                }
                if (!ClearExactControllerSlot(device, index)) return false;
                ReleaseHidHideManagedDevice(device);
                return true;
            }
            if (!legacyHidSlotAuthority.TryBeginRetirement(binding,
                    out ControlServiceLegacyHidSlotFailure beginFailure,
                    out InputControllerSlotTableFailure tableFailure))
            {
                StartupDiag($"Typed legacy stop retirement rejected index={index}: {beginFailure}/{tableFailure}");
                return false;
            }
            TryClaimControllerRemoval(device);

            if (!legacyHidSlotAuthority.TryPublishTerminalNeutral(binding,
                    () =>
                    {
                        if (!immediateUnplug)
                        {
                            DS4LightBar.forcelight[index] = false;
                            DS4LightBar.forcedFlash[index] = 0;
                            DS4LightBar.defaultLight = true;
                            DS4LightBar.updateLightBar(device, index);
                        }
                        RetireControllerPresentation(device, index,
                            commitNeutralMapping: false, logRemoval: false);
                        CommitNeutralMapping(index);
                    },
                    InputControllerRegistration.MaximumStopTimeoutMilliseconds,
                    out ControlServiceLegacyHidSlotFailure neutralFailure,
                    out tableFailure))
            {
                StartupDiag($"Typed legacy stop terminal neutral failed index={index}: {neutralFailure}/{tableFailure}");
                return false;
            }

            Action<DS4Device> afterStop = null;
            if ((DCBTatStop && !device.isCharging()) || suspending)
            {
                if (device.getConnectionType() == ConnectionType.BT)
                {
                    afterStop = static stopped => stopped.DisconnectBT(true);
                }
                else if (device.getConnectionType() == ConnectionType.SONYWA)
                {
                    afterStop = static stopped =>
                        stopped.DisconnectDongle(true);
                }
            }
            if (!legacyHidSlotAuthority.TryFinalizeRetirement(binding,
                    InputControllerRegistration.MaximumStopTimeoutMilliseconds,
                    out ControlServiceLegacyHidSlotFailure retireFailure,
                    out tableFailure, afterStop))
            {
                StartupDiag($"Typed legacy stop final retirement failed index={index}: {retireFailure}/{tableFailure}");
                return false;
            }
            Thread.Sleep(50);
            if (!ClearExactControllerSlot(device, index)) return false;
            ReleaseHidHideManagedDevice(device);
            return true;
        }

        internal bool TryCaptureProfileActionTarget(int slot, DS4Device source,
            out ControllerProfileActionTarget target) =>
            ControllerProfileActionTarget.TryCapture(this, inputRegistrationTable,
                slot, source, out target);

        private int FindExactControllerSlot(DS4Device device)
        {
            for (int index = 0; index < DS4Controllers.Length; index++)
            {
                if (ReferenceEquals(DS4Controllers[index], device))
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool TryClaimControllerRemoval(DS4Device device)
        {
            lock (device.removeLocker)
            {
                if (device.IsRemoving)
                {
                    return false;
                }
                device.IsRemoving = true;
                return true;
            }
        }

        private void RetireControllerPresentation(DS4Device device, int index,
            bool commitNeutralMapping, bool logRemoval = true)
        {
            if (!ReferenceEquals(DS4Controllers[index], device))
            {
                return;
            }
            DeactivateGameBarCompatibilityOutput(index);
            CurrentState[index].Battery = PreviousState[index].Battery = 0;
            if (!useDInputOnly[index])
            {
                UnplugOutDev(index, device);
            }
            else if (!device.PrimaryDevice)
            {
                OutputDevice outDev = outputDevices[index];
                if (outDev != null)
                {
                    outDev.RemoveFeedback(index);
                    outputDevices[index] = null;
                }
            }
            if (commitNeutralMapping)
            {
                CommitNeutralMapping(index);
            }

            if (logRemoval)
            {
                string removed = DS4WinWPF.Properties.Resources.
                    ControllerWasRemoved.Replace("*Mac address*",
                        (index + 1).ToString());
                if (device.getBattery() <= 20 &&
                    device.getConnectionType() == ConnectionType.BT &&
                    !device.isCharging())
                {
                    removed += ". " +
                        DS4WinWPF.Properties.Resources.ChargeController;
                }
                LogDebug(removed);
                AppLogger.LogToTray(removed);
            }
            dualSenseAudioPassthrough.Stop(index);
            dualShock4AudioPassthrough.Stop(index);
            dualSenseMicrophonePassthrough.Stop();
            audioHapticsService.Stop(index);
            DisconnectPlayStationFeatureOutput(index);
        }

        private static void CommitNeutralMapping(int index)
        {
            Task.Run(() => Mapping.Commit(index)).Wait();
        }

        private bool ClearExactControllerSlot(DS4Device device, int index)
        {
            if (!ReferenceEquals(DS4Controllers[index], device))
            {
                return false;
            }
            if (!TryRetireMouseCallbacks(index, device)) return false;
            device.IsRemoved = true;
            device.Synced = false;
            Mapping.RequestPostMapStickReset(index);
            oscState[index] = new DS4State();
            slotManager.RemoveController(device, index);
            if (isUsingOSCSender())
            {
                oscSender.Send(new SharpOSC.OscMessage(
                    "/ds4windows/monitor/" + index + "/plug", 0));
            }
            touchPad[index] = null;
            lag[index] = false;
            inWarnMonitor[index] = false;
            useDInputOnly[index] = true;
            Global.activeOutDevType[index] = OutContType.None;
            return inputSlotAdmission.TryReleaseLegacySlot(index, device);
        }

        public bool[] lag = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        public bool[] inWarnMonitor = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private byte[] currentBattery = new byte[MAX_DS4_CONTROLLER_COUNT] { 0, 0, 0, 0, 0, 0, 0, 0 };
        private bool[] charging = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] tempStrings = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private DateTime[] gameBarHomeButtonIgnoreUntilUtc = new DateTime[MAX_DS4_CONTROLLER_COUNT];
        private readonly OutputDevice[] gameBarCompatibilityOutputDevices = new OutputDevice[MAX_DS4_CONTROLLER_COUNT];
        private readonly int[] gameBarCompatibilityRoutingActive = new int[MAX_DS4_CONTROLLER_COUNT];
        private readonly DateTime[] gameBarCompatibilityNextRetryUtc = new DateTime[MAX_DS4_CONTROLLER_COUNT];
        private readonly long[] gameBarCompatibilityPrewarmUntilTicks = new long[MAX_DS4_CONTROLLER_COUNT];
        private readonly object gameBarCompatibilityOutputLock = new object();

        private DateTime gameBarLastVisibleUtc = DateTime.MinValue;
        private DateTime gameBarLastVisibilityCheckUtc = DateTime.MinValue;
        private bool gameBarVerboseDetectionLogInitialized = false;
        private bool gameBarVerboseLastVisible = false;
        private DateTime gameBarVerboseLastDetectionLogUtc = DateTime.MinValue;
        private bool[] dualSenseMuteButtonWasDown = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private bool[] dualSenseMuteLedOn = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private readonly InputDevices.DualSenseDevice[]
            dualSenseMuteOutputDevices =
                new InputDevices.DualSenseDevice[MAX_DS4_CONTROLLER_COUNT];
        private readonly int[] dualSenseMuteOutputSignatures =
        {
            -1, -1, -1, -1, -1, -1, -1, -1,
        };
        private readonly object[] dualSenseMuteProfileLocks = new object[MAX_DS4_CONTROLLER_COUNT]
        {
            new object(), new object(), new object(), new object(),
            new object(), new object(), new object(), new object(),
        };
        private bool[] dualSenseMuteProfilePending = new bool[MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        private string[] dualSenseMuteRequestedProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private long[] dualSenseMuteRequestedModeEpoch =
            new long[MAX_DS4_CONTROLLER_COUNT];
        private string[] dualSenseMuteRememberedOffProfileName = new string[MAX_DS4_CONTROLLER_COUNT] { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };

        private readonly ControllerVirtualOutputAttempt[] virtualOutputAttempts =
            new ControllerVirtualOutputAttempt[MAX_DS4_CONTROLLER_COUNT];

        public ControllerRuntimeSignals GetControllerRuntimeSignals(int index)
        {
            if (index < 0 || index >= CURRENT_DS4_CONTROLLER_LIMIT)
            {
                return new ControllerRuntimeSignals(false, false, false,
                    false, false, false,
                    ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    ControllerRuntimeLaneState.NotRequired,
                    "virtual controller");
            }

            DS4Device device = DS4Controllers[index];
            bool physicalCleanupQuarantined = ControllerRuntimeStatusPolicy.
                HasQuarantinedPhysicalRuntime(device, index, inputRegistrationTable);
            bool physicalPresent = device != null && (!device.IsRemoving || physicalCleanupQuarantined);
            bool physicalSynced = physicalPresent && device.isSynced();
            bool physicalAlive = physicalSynced && device.IsAlive();
            bool virtualRequired = !Global.getDInputOnly(index);
            OutContType desiredType = Global.OutContType[index].Normalize();
            ViiperOutDevice viiperOutput = outputDevices[index] as ViiperOutDevice;
            ViiperOutDevice playStationFeatureOutput =
                GetPlayStationFeatureOutput(index);
            OutContType playStationFeatureOutputType =
                playStationFeatureOutput?.OutputType ?? OutContType.None;
            bool virtualConnected = !virtualRequired ||
                viiperOutput?.IsRuntimeConnected == true;
            bool virtualTypeMatches = !virtualRequired ||
                Global.activeOutDevType[index].Normalize() == desiredType;
            ControllerVirtualOutputAttempt outputAttempt = virtualOutputAttempts == null ? null :
                Volatile.Read(ref virtualOutputAttempts[index]);
            bool attemptMatches = outputAttempt?.Matches(physicalPresent ? device : null,
                desiredType, virtualRequired) == true;
            if (outputAttempt != null && !attemptMatches)
                Interlocked.CompareExchange(ref virtualOutputAttempts[index], null, outputAttempt);
            bool virtualFailed = virtualRequired &&
                ((attemptMatches && outputAttempt.Failed) || viiperOutput?.HasRuntimeFault == true);

            bool advancedHapticsRequired = virtualRequired &&
                (desiredType == OutContType.ViiperDualSense ||
                    desiredType == OutContType.ViiperDualSenseEdge);
            ViiperOutDevice advancedHapticsOutput =
                playStationFeatureOutput ?? viiperOutput;
            ControllerRuntimeLaneState advancedHaptics =
                !advancedHapticsRequired
                    ? ControllerRuntimeLaneState.NotRequired
                    : advancedHapticsOutput?.SupportsAtomicAudioHaptics == true
                        ? ControllerRuntimeLaneState.Ready
                        : virtualConnected
                            ? ControllerRuntimeLaneState.Unavailable
                            : ControllerRuntimeLaneState.Starting;

            // A shared profile can retain Sony media settings while running
            // on Switch 2 or another controller. Require a lane only when the
            // exact physical model/transport supports it; missing runtime
            // readiness on supported hardware still remains an error.
            bool controllerAudioApplicable = physicalPresent &&
                ControllerAudioCapabilityPolicy.SupportsControllerAudio(device);
            bool speakerRequired = controllerAudioApplicable &&
                IsControllerSpeakerEnabled(index);
            ControllerRuntimeLaneState speaker =
                ControllerRuntimeLaneState.NotRequired;
            if (speakerRequired)
            {
                speaker = device is InputDevices.DualSenseDevice
                    ? dualSenseAudioPassthrough.GetStatus(index)
                    : dualShock4AudioPassthrough.GetStatus(index);
            }

            bool microphoneRequired = controllerAudioApplicable &&
                Global.DualSenseEnableMicrophonePassthrough[index];
            ControllerRuntimeLaneState microphone =
                ControllerRuntimeLaneState.NotRequired;
            if (microphoneRequired)
            {
                bool directMicrophone =
                    ControllerMicrophoneRoutePolicy.CanRouteDirectViiperMicrophone(
                        true, device, playStationFeatureOutputType,
                        playStationFeatureOutput);
                if (directMicrophone)
                {
                    microphone = playStationFeatureOutput?
                        .SupportsActiveVirtualMicrophone == true
                        ? ControllerRuntimeLaneState.Ready
                        : playStationFeatureOutput?.IsRuntimeConnected == true
                            ? ControllerRuntimeLaneState.Unavailable
                            : ControllerRuntimeLaneState.Starting;
                }
                else if (device is InputDevices.DualSenseDevice)
                {
                    microphone = dualSenseMicrophonePassthrough.IsRunningFor(
                            Global.DualSenseMicrophoneCaptureEndpointId[index],
                            Global.DualSenseMicrophoneOutputEndpointId[index])
                        ? ControllerRuntimeLaneState.Ready
                        : ControllerRuntimeLaneState.Unavailable;
                }
                else
                {
                    microphone = ControllerRuntimeLaneState.Unavailable;
                }
            }

            // Match AudioHapticsService.Start's physical-device applicability.
            // This is separate from native feedback translated to HD rumble.
            bool audioHapticsRequired = physicalPresent &&
                device is InputDevices.DualSenseDevice &&
                Global.store.audioHapticsSettings[index]?.Enabled == true;
            ControllerRuntimeLaneState audioHaptics =
                ControllerRuntimeLaneState.NotRequired;
            if (audioHapticsRequired)
            {
                AudioHapticsRuntimeStatus status =
                    audioHapticsService.GetStatus(index);
                audioHaptics = status.Active
                    ? ControllerRuntimeLaneState.Ready
                    : status.Message.IndexOf("starting",
                        StringComparison.OrdinalIgnoreCase) >= 0
                        ? ControllerRuntimeLaneState.Starting
                        : ControllerRuntimeLaneState.Unavailable;
            }

            return new ControllerRuntimeSignals(physicalPresent,
                physicalSynced, physicalAlive, virtualRequired,
                virtualConnected, virtualTypeMatches, advancedHaptics,
                speaker, microphone, audioHaptics,
                desiredType.ToDisplayName(), virtualFailed, physicalCleanupQuarantined);
        }

        internal static bool ShouldUseGameBarControllerCompatibility(bool enabled,
            OutContType outputType, bool dInputOnly)
        {
            return enabled && !dInputOnly &&
                (outputType == OutContType.ViiperDualSense ||
                outputType == OutContType.ViiperDualSenseEdge ||
                outputType == OutContType.ViiperDS4);
        }

        internal static bool ShouldRetireGameBarCompatibilityBeforeProfileChange(
            bool routeActive, bool enabled, OutContType requestedOutputType,
            bool requestedDInputOnly)
        {
            return routeActive &&
                !ShouldUseGameBarControllerCompatibility(enabled,
                    requestedOutputType.Normalize(), requestedDInputOnly);
        }

        /// <summary>
        /// Reconciles the temporary XInput route with a profile's requested
        /// output before that profile unplugs or creates its native device.
        /// Waiting for the periodic Game Bar visibility poll leaves a window
        /// where reports still target the old companion while a new native
        /// Xbox pad is already visible. Game Bar can bind that stale pad and
        /// then lose all input when the timer eventually removes it.
        /// </summary>
        internal void PrepareGameBarCompatibilityProfileTransition(int index,
            OutContType requestedOutputType, bool requestedDInputOnly)
        {
            if (index < 0 || index >= MAX_DS4_CONTROLLER_COUNT)
            {
                return;
            }

            lock (gameBarCompatibilityOutputLock)
            {
                bool routeActive = Volatile.Read(
                    ref gameBarCompatibilityRoutingActive[index]) == 1;
                if (!ShouldRetireGameBarCompatibilityBeforeProfileChange(
                        routeActive,
                        Global.GameBarControllerCompatibility[index],
                        requestedOutputType, requestedDInputOnly))
                {
                    return;
                }

                Interlocked.Exchange(
                    ref gameBarCompatibilityPrewarmUntilTicks[index], 0);
                DeactivateGameBarCompatibilityOutputCore(index);
                StartupDiag(
                    $"GameBar compatibility retired before profile output transition controller={index + 1} requested={requestedOutputType.Normalize()}");
            }
        }

        private bool HasAnyConfiguredGameBarCompatibility()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (DS4Controllers[i] != null &&
                    ShouldUseGameBarControllerCompatibility(
                        Global.GameBarControllerCompatibility[i],
                        Global.OutContType[i], getDInputOnly(i)))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsAnyGameBarCompatibilityActive()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (Volatile.Read(ref gameBarCompatibilityRoutingActive[i]) == 1)
                {
                    return true;
                }
            }

            return false;
        }

        private OutputDevice GetReportOutputDevice(int index)
        {
            // The companion pointer is published before routing becomes active
            // and routing is disabled before the pointer is withdrawn. This
            // keeps the report path valid throughout VIIPER's comparatively
            // slow USB/IP plug and unplug operations.
            if (Volatile.Read(ref gameBarCompatibilityRoutingActive[index]) == 1)
            {
                OutputDevice compatibilityOutput = Volatile.Read(
                    ref gameBarCompatibilityOutputDevices[index]);
                if (compatibilityOutput != null)
                {
                    return compatibilityOutput;
                }
            }

            return outputDevices[index];
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

            if (ShouldUseGameBarControllerCompatibility(
                Global.GameBarControllerCompatibility[ind],
                Global.OutContType[ind], getDInputOnly(ind)))
            {
                cState.PS = false;
                tempControlState.PS = false;
                gameBarHomeButtonIgnoreUntilUtc[ind] = now + TimeSpan.FromSeconds(1);
                // A USB/IP attach is slow enough to make the first Game Bar
                // interaction visibly hitch. Prewarm off the controller report
                // thread, then open the overlay only after XInput is available.
                Interlocked.Exchange(
                    ref gameBarCompatibilityPrewarmUntilTicks[ind],
                    Environment.TickCount64 + 2000);
                _ = Task.Run(() =>
                {
                    ActivateGameBarCompatibilityOutput(ind);
                    string openResult = gameBarIntegration.OpenGameBar();
                    StartupDiag($"GameBar compatibility home button controller={ind + 1} {openResult}");
                });
                return;
            }

            // Profiles that do not request the modern compatibility route use
            // their normal Home mapping. There is deliberately no legacy
            // profile-switch fallback here.
        }

        private void UpdateGameBarCompatibilityOutputs(bool gameBarVisible)
        {
            long nowTicks = Environment.TickCount64;
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (gameBarVisible)
                {
                    // Once visibility is confirmed, the overlay owns the route.
                    // Closing it can then remove the companion immediately.
                    Interlocked.Exchange(
                        ref gameBarCompatibilityPrewarmUntilTicks[i], 0);
                }

                bool shouldRoute = ShouldKeepGameBarCompatibilityRoute(
                        gameBarVisible, nowTicks,
                        Interlocked.Read(
                            ref gameBarCompatibilityPrewarmUntilTicks[i])) &&
                    DS4Controllers[i] != null &&
                    ShouldUseGameBarControllerCompatibility(
                        Global.GameBarControllerCompatibility[i],
                        Global.OutContType[i], getDInputOnly(i));
                if (shouldRoute)
                {
                    ActivateGameBarCompatibilityOutput(i);
                }
                else
                {
                    DeactivateGameBarCompatibilityOutput(i);
                }
            }
        }

        internal static bool ShouldKeepGameBarCompatibilityRoute(
            bool gameBarVisible, long nowTicks, long prewarmUntilTicks)
        {
            return gameBarVisible || nowTicks < prewarmUntilTicks;
        }

        private void ActivateGameBarCompatibilityOutput(int index)
        {
            lock (gameBarCompatibilityOutputLock)
            {
                ActivateGameBarCompatibilityOutputCore(index);
            }
        }

        private void ActivateGameBarCompatibilityOutputCore(int index)
        {
            if (!running ||
                Volatile.Read(ref gameBarCompatibilityRoutingActive[index]) == 1 ||
                DateTime.UtcNow < gameBarCompatibilityNextRetryUtc[index])
            {
                return;
            }

            DS4Device source = DS4Controllers[index];
            OutputDevice nativeOutput = outputDevices[index];
            if (source == null || nativeOutput == null)
            {
                return;
            }

            if (outputslotMan.FindOpenSlot() == null)
            {
                gameBarCompatibilityNextRetryUtc[index] =
                    DateTime.UtcNow + TimeSpan.FromSeconds(2);
                StartupDiag($"GameBar compatibility activation delayed controller={index + 1} reason=no-output-slot");
                return;
            }

            OutputDevice compatibilityOutput = null;
            try
            {
                compatibilityOutput = EstablishOutDevice(index, OutContType.ViiperX360);
                if (compatibilityOutput == null)
                {
                    throw new InvalidOperationException(
                        "Could not create the temporary XInput output.");
                }

                outputslotMan.DeferredPlugin(compatibilityOutput, -1,
                    $"Game Bar compatibility for controller {index + 1}",
                    outputDevices, OutContType.ViiperX360);
                if (outputslotMan.GetOutSlotDevice(compatibilityOutput) == null)
                {
                    throw new InvalidOperationException(
                        "The temporary XInput output was not assigned to a slot.");
                }

                Interlocked.Exchange(
                    ref gameBarCompatibilityOutputDevices[index], compatibilityOutput);
                // Commit routing only after the companion is fully connected
                // and published. The native output continues receiving reports
                // during the whole USB/IP creation interval.
                Interlocked.Exchange(ref gameBarCompatibilityRoutingActive[index], 1);
                try
                {
                    nativeOutput.ResetState();
                }
                catch (Exception resetEx)
                {
                    // The companion is already live. A native neutral-report
                    // failure must not roll back or tear down the valid route.
                    StartupDiag($"GameBar compatibility native reset failed controller={index + 1} {resetEx.GetType().Name}: {resetEx.Message}");
                }
                gameBarCompatibilityNextRetryUtc[index] = DateTime.MinValue;
                StartupDiag($"GameBar compatibility activated controller={index + 1} native={Global.OutContType[index]} companion=X360");
            }
            catch (Exception ex)
            {
                if (compatibilityOutput != null &&
                    outputslotMan.GetOutSlotDevice(compatibilityOutput) != null)
                {
                    outputslotMan.DeferredRemoval(compatibilityOutput, -1,
                        outputDevices, true);
                }

                Interlocked.Exchange(
                    ref gameBarCompatibilityOutputDevices[index], null);
                Interlocked.Exchange(ref gameBarCompatibilityRoutingActive[index], 0);
                gameBarCompatibilityNextRetryUtc[index] =
                    DateTime.UtcNow + TimeSpan.FromSeconds(2);
                StartupDiag($"GameBar compatibility activation failed controller={index + 1} {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void DeactivateGameBarCompatibilityOutput(int index)
        {
            lock (gameBarCompatibilityOutputLock)
            {
                DeactivateGameBarCompatibilityOutputCore(index);
            }
        }

        private void DeactivateGameBarCompatibilityOutputCore(int index)
        {
            gameBarCompatibilityNextRetryUtc[index] = DateTime.MinValue;
            // Return the report path to the native output before withdrawing or
            // disconnecting the companion. Reports never observe a null route.
            Interlocked.Exchange(ref gameBarCompatibilityRoutingActive[index], 0);
            OutputDevice compatibilityOutput = Interlocked.Exchange(
                ref gameBarCompatibilityOutputDevices[index], null);
            if (compatibilityOutput == null)
            {
                return;
            }

            try
            {
                compatibilityOutput?.ResetState();
                if (compatibilityOutput != null &&
                    outputslotMan.GetOutSlotDevice(compatibilityOutput) != null)
                {
                    outputslotMan.DeferredRemoval(compatibilityOutput, -1,
                        outputDevices, true);
                }
            }
            catch (Exception ex)
            {
                StartupDiag($"GameBar compatibility removal failed controller={index + 1} {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref gameBarCompatibilityRoutingActive[index], 0);
            }

            StartupDiag($"GameBar compatibility deactivated controller={index + 1} native={Global.OutContType[index]}");
        }

        private void StopAllGameBarCompatibilityOutputs()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                DeactivateGameBarCompatibilityOutput(i);
            }
        }

        public void UpdateGameBarState()
        {
            if (!running)
            {
                return;
            }

            if (Interlocked.Exchange(ref gameBarStateUpdateGate, 1) == 1)
            {
                return;
            }

            try
            {
                bool anyMutePending = HasAnyPendingDualSenseMuteProfile();
                bool anyCompatibilityConfigured = HasAnyConfiguredGameBarCompatibility();
                bool anyCompatibilityActive = IsAnyGameBarCompatibilityActive();

                if (!anyMutePending && !anyCompatibilityConfigured &&
                    !anyCompatibilityActive)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (now - gameBarLastVisibilityCheckUtc < TimeSpan.FromMilliseconds(100))
                {
                    return;
                }

                gameBarLastVisibilityCheckUtc = now;
                bool gameBarVisible = gameBarIntegration.IsGameBarVisible();
                LogGameBarDetectionIfVerbose(now, gameBarVisible,
                    anyCompatibilityConfigured, anyCompatibilityActive);
                if (gameBarVisible)
                {
                    gameBarLastVisibleUtc = now;
                    UpdateGameBarCompatibilityOutputs(true);
                    return;
                }

                ProcessPendingDualSenseMuteProfiles();
                // Publish the native route before removing the companion so
                // the report path never observes a missing output device.
                UpdateGameBarCompatibilityOutputs(false);
            }
            catch (Exception ex)
            {
                StartupDiag($"UpdateGameBarState exception {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref gameBarStateUpdateGate, 0);
            }
        }

        private void LogGameBarDetectionIfVerbose(DateTime now, bool gameBarVisible,
            bool anyCompatibilityConfigured, bool anyCompatibilityActive)
        {
            if (!Global.VerboseStartupLogging)
            {
                return;
            }

            bool shouldLog = !gameBarVerboseDetectionLogInitialized ||
                gameBarVisible != gameBarVerboseLastVisible ||
                now - gameBarVerboseLastDetectionLogUtc > TimeSpan.FromSeconds(30);

            if (!shouldLog)
            {
                return;
            }

            gameBarVerboseDetectionLogInitialized = true;
            gameBarVerboseLastVisible = gameBarVisible;
            gameBarVerboseLastDetectionLogUtc = now;
            StartupDiag($"GameBar detection visible={gameBarVisible} compatibilityConfigured={anyCompatibilityConfigured} compatibilityActive={anyCompatibilityActive} " +
                $"{gameBarIntegration.CaptureLastDetectionSummary()} controllers={BuildGameBarPriorityStateSummary()}");
        }

        private string BuildGameBarPriorityStateSummary()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                if (DS4Controllers[i] == null &&
                    Volatile.Read(ref gameBarCompatibilityRoutingActive[i]) == 0)
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
                builder.Append(",compatibility=");
                builder.Append(Global.GameBarControllerCompatibility[i]);
                builder.Append(",compatibilityActive=");
                builder.Append(Volatile.Read(ref gameBarCompatibilityRoutingActive[i]) == 1);
                builder.Append("]");
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private bool HasAnyPendingDualSenseMuteProfile()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                lock (dualSenseMuteProfileLocks[i])
                {
                    if (dualSenseMuteProfilePending[i])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void QueueDualSenseMuteProfile(int ind, string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                return;
            }

            string profilePath = Path.Combine(appdatapath, "Profiles", $"{profileName}.xml");
            if (!File.Exists(profilePath))
            {
                LogDebug($"DualSense mute profile action skipped. Profile '{profileName}' was not found.", true);
                return;
            }

            long modeEpoch =
                Global.ReadDualSenseMuteButtonModeEpoch(ind);
            lock (dualSenseMuteProfileLocks[ind])
            {
                dualSenseMuteRequestedProfileName[ind] = profileName;
                dualSenseMuteRequestedModeEpoch[ind] = modeEpoch;
                dualSenseMuteProfilePending[ind] = true;
            }
        }

        private void ProcessPendingDualSenseMuteProfiles()
        {
            for (int i = 0; i < MAX_DS4_CONTROLLER_COUNT; i++)
            {
                string profileName;
                long modeEpoch;
                lock (dualSenseMuteProfileLocks[i])
                {
                    if (!dualSenseMuteProfilePending[i])
                    {
                        continue;
                    }

                    profileName = dualSenseMuteRequestedProfileName[i];
                    modeEpoch = dualSenseMuteRequestedModeEpoch[i];
                    dualSenseMuteProfilePending[i] = false;
                    dualSenseMuteRequestedProfileName[i] = string.Empty;
                    dualSenseMuteRequestedModeEpoch[i] = 0;
                }

                if (!IsCurrentDualSenseMuteProfileRequest(i, modeEpoch))
                {
                    // A profile or live editor change can enable the master
                    // after this request was queued. Recheck at execution so
                    // stale work can never escape input/output mode.
                    continue;
                }

                int deviceIndex = i;
                Mapping.RequestTemporaryProfileLoad(deviceIndex, profileName,
                    false, this, loaded =>
                    {
                        if (!loaded)
                        {
                            LogDebug($"DualSense mute profile action failed to load " +
                                $"'{profileName}'.", true);
                        }
                    }, loadGuard: () =>
                        IsCurrentDualSenseMuteProfileRequest(
                            deviceIndex, modeEpoch));
            }
        }

        internal static bool IsCurrentDualSenseMuteProfileRequest(
            int device, long modeEpoch)
        {
            return Global.IsCurrentDualSenseMuteButtonModeEpoch(
                    device, modeEpoch) &&
                ResolveDualSenseMuteButtonPolicy(device).SwitchesProfiles;
        }

        internal static string UpdateDualSenseRememberedOffProfileName(
            string rememberedOffProfileName, bool controllerConnected,
            bool inputOutputModeEnabled)
        {
            // ResetProfile temporarily exposes a no-handler policy before the
            // target profile is mapped. That transient must not erase the
            // source profile needed by a blank mute-off target. Only a real
            // disconnect or explicit takeover by input/output mode ends the
            // profile-switch lifecycle.
            return controllerConnected && !inputOutputModeEnabled ?
                rememberedOffProfileName ?? string.Empty : string.Empty;
        }

        internal static string ResolveDualSenseMuteOffProfileName(
            string configuredOffProfileName,
            string rememberedOffProfileName)
        {
            return string.IsNullOrEmpty(configuredOffProfileName) ?
                rememberedOffProfileName ?? string.Empty :
                configuredOffProfileName;
        }

        private static DualSenseMuteButtonRuntimePolicy
            ResolveDualSenseMuteButtonPolicy(int ind)
        {
            return DualSenseMuteButtonRuntimePolicy.Resolve(
                Global.DualSenseMuteButtonMutesInputOutput[ind],
                Global.DualSenseMuteButtonMutesMicrophone[ind],
                Global.DualSenseMuteButtonMutesSpeaker[ind],
                Global.DualSenseMuteButtonSwitchesProfiles[ind],
                Global.DualSenseMuteButtonLightEnabled[ind]);
        }

        internal static bool IsCurrentDualSenseMuteOutputPublication(
            InputDevices.DualSenseDevice cachedDevice, int cachedSignature,
            InputDevices.DualSenseDevice currentDevice, int currentSignature)
        {
            return ReferenceEquals(cachedDevice, currentDevice) &&
                cachedSignature == currentSignature;
        }

        internal static void InvalidateDualSenseMuteOutputSignature(
            ref int cachedSignature)
        {
            Interlocked.Exchange(ref cachedSignature, -1);
        }

        private bool ApplyDualSenseMuteButtonOutputState(int ind,
            InputDevices.DualSenseDevice dualSenseDevice,
            in DualSenseMuteButtonRuntimePolicy policy,
            bool controllerAudioEnabled, bool headsetOnly)
        {
            bool muteLatched = dualSenseMuteLedOn[ind];
            bool speakerMuteOverride = policy.CanMuteBuiltInSpeaker(
                controllerAudioEnabled, headsetOnly);
            bool builtInSpeakerEnabled = controllerAudioEnabled &&
                !headsetOnly;
            byte configuredSpeakerVolume = builtInSpeakerEnabled ?
                DualSenseSpeakerVolume[ind] : (byte)0;
            byte resolvedSpeakerVolume = policy.ResolveSpeakerVolume(
                configuredSpeakerVolume, muteLatched);
            bool microphoneMuted = policy.MutesMicrophone && muteLatched;
            bool speakerMuted = speakerMuteOverride && muteLatched;
            int publicationSignature = resolvedSpeakerVolume << 8 |
                (policy.OverridesMuteLed ? 1 : 0) |
                (muteLatched ? 1 << 1 : 0) |
                (policy.MutesMicrophone ? 1 << 2 : 0) |
                (microphoneMuted ? 1 << 3 : 0) |
                (speakerMuteOverride ? 1 << 4 : 0) |
                (speakerMuted ? 1 << 5 : 0);
            if (IsCurrentDualSenseMuteOutputPublication(
                    Volatile.Read(ref dualSenseMuteOutputDevices[ind]),
                    Volatile.Read(ref dualSenseMuteOutputSignatures[ind]),
                    dualSenseDevice, publicationSignature))
            {
                return false;
            }

            dualSenseDevice.SetProfileMuteButtonState(
                policy.OverridesMuteLed,
                muteLatched,
                policy.MutesMicrophone,
                microphoneMuted,
                speakerMuteOverride,
                speakerMuted,
                resolvedSpeakerVolume);
            Volatile.Write(ref dualSenseMuteOutputDevices[ind],
                dualSenseDevice);
            Volatile.Write(ref dualSenseMuteOutputSignatures[ind],
                publicationSignature);
            return true;
        }

        private void CheckDualSenseMuteButtonProfileActions(int ind, DS4State cState)
        {
            if (!(DS4Controllers[ind] is InputDevices.DualSenseDevice dualSenseDevice))
            {
                bool hadDualSenseOutputDevice =
                    dualSenseMuteOutputDevices[ind] != null;
                dualSenseMuteButtonWasDown[ind] = false;
                dualSenseMuteOutputDevices[ind] = null;
                dualSenseMuteOutputSignatures[ind] = -1;
                dualSenseMuteRememberedOffProfileName[ind] =
                    UpdateDualSenseRememberedOffProfileName(
                        dualSenseMuteRememberedOffProfileName[ind],
                        controllerConnected: false,
                        inputOutputModeEnabled: false);
                if (hadDualSenseOutputDevice)
                {
                    lock (dualSenseMuteProfileLocks[ind])
                    {
                        dualSenseMuteProfilePending[ind] = false;
                        dualSenseMuteRequestedProfileName[ind] = string.Empty;
                        dualSenseMuteRequestedModeEpoch[ind] = 0;
                    }
                }
                return;
            }

            DualSenseMuteButtonRuntimePolicy policy =
                ResolveDualSenseMuteButtonPolicy(ind);
            if (!policy.HandlesButton)
            {
                ApplyDualSenseMuteButtonOutputState(ind, dualSenseDevice,
                    policy, IsControllerSpeakerEnabled(ind),
                    IsControllerHeadsetOnlyAudio(ind));
                dualSenseMuteButtonWasDown[ind] = cState.Mute;
                dualSenseMuteRememberedOffProfileName[ind] =
                    UpdateDualSenseRememberedOffProfileName(
                        dualSenseMuteRememberedOffProfileName[ind],
                        controllerConnected: true,
                        inputOutputModeEnabled:
                            policy.InputOutputModeEnabled);
                return;
            }

            bool muteDown = cState.Mute;
            if (muteDown && !dualSenseMuteButtonWasDown[ind])
            {
                dualSenseMuteLedOn[ind] = !dualSenseMuteLedOn[ind];

                if (policy.SwitchesProfiles)
                {
                    string requestedProfileName;
                    if (dualSenseMuteLedOn[ind])
                    {
                        requestedProfileName = Global.DualSenseMuteOnProfileName[ind];
                        dualSenseMuteRememberedOffProfileName[ind] = Global.DualSenseMuteOffProfileName[ind];
                    }
                    else
                    {
                        requestedProfileName =
                            ResolveDualSenseMuteOffProfileName(
                                Global.DualSenseMuteOffProfileName[ind],
                                dualSenseMuteRememberedOffProfileName[ind]);
                    }

                    QueueDualSenseMuteProfile(ind, requestedProfileName);
                }
            }

            bool muteOutputStateChanged =
                ApplyDualSenseMuteButtonOutputState(ind, dualSenseDevice,
                    policy, IsControllerSpeakerEnabled(ind),
                    IsControllerHeadsetOnlyAudio(ind));
            if (policy.InputOutputModeEnabled)
            {
                // Cancel a profile action queued by the previous settings or
                // profile before the periodic dispatcher can observe it.
                if (muteOutputStateChanged)
                {
                    lock (dualSenseMuteProfileLocks[ind])
                    {
                        dualSenseMuteProfilePending[ind] = false;
                        dualSenseMuteRequestedProfileName[ind] = string.Empty;
                        dualSenseMuteRequestedModeEpoch[ind] = 0;
                    }
                }
                dualSenseMuteRememberedOffProfileName[ind] =
                    UpdateDualSenseRememberedOffProfileName(
                        dualSenseMuteRememberedOffProfileName[ind],
                        controllerConnected: true,
                        inputOutputModeEnabled: true);
                dualSenseMuteButtonWasDown[ind] = muteDown;
                return;
            }

            dualSenseMuteButtonWasDown[ind] = muteDown;
        }

        private void StartGameBarStateTimer()
        {
            if (gameBarStateTimer != null)
            {
                return;
            }

            gameBarStateTimer = new System.Threading.Timer(_ => UpdateGameBarState(),
                null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        private void StopGameBarStateTimer()
        {
            System.Threading.Timer timer = Interlocked.Exchange(ref gameBarStateTimer, null);
            timer?.Dispose();
        }

        // Called every time a new input report has arrived
        protected void On_Report(DS4Device device, EventArgs e, int ind) =>
            On_Report(device, e, ind, null);

        private void On_Report(DS4Device device, EventArgs e, int ind,
            ReportDiagnosticsWorker.Source diagnosticsSource)
        {
            if ((uint)ind < (uint)DS4Controllers.Length)
            {
                OutContType normalizedOutput = activeOutDevType[ind].Normalize();
                ReportDiagnosticsSnapshot deferredDiagnostics = new()
                {
                    Controller = ind,
                    Device = device,
                    ActiveOutput = normalizedOutput,
                    Latency = device.Latency,
                };
                int startupReportCount = 0;
                bool startupReportDiag = false;
                if (Global.VerboseStartupLogging)
                {
                    startupReportCount = ++startupReportDiagCounts[ind];
                    startupReportDiag = startupReportCount <= 5 || startupReportCount == 50;
                    if (startupReportDiag)
                    {
                        deferredDiagnostics.StartupDiagnostic = true;
                        deferredDiagnostics.StartupReportCount =
                            startupReportCount;
                        deferredDiagnostics.StartupLatency = device.Latency;
                        deferredDiagnostics.Synced = device.isSynced();
                        deferredDiagnostics.UseDInputOnly =
                            useDInputOnly[ind];
                    }
                }

                string devError = tempStrings[ind] = device.error;
                if (!string.IsNullOrEmpty(devError))
                {
                    deferredDiagnostics.DeviceError = devError;
                }

                if (inWarnMonitor[ind])
                {
                    int flashWhenLateAt = getFlashWhenLateAt();
                    if (!lag[ind] && device.Latency >= flashWhenLateAt)
                    {
                        lag[ind] = true;
                        // Lightbar state remains immediate; only its log is deferred.
                        ApplyLagFlashState(device, ind, true);
                        deferredDiagnostics.LagChanged = true;
                        deferredDiagnostics.LagOn = true;
                        deferredDiagnostics.Latency = device.Latency;
                    }
                    else if (lag[ind] && device.Latency < flashWhenLateAt)
                    {
                        lag[ind] = false;
                        // Lightbar state remains immediate; only its log is deferred.
                        ApplyLagFlashState(device, ind, false);
                        deferredDiagnostics.LagChanged = true;
                        deferredDiagnostics.LagOn = false;
                        deferredDiagnostics.Latency = device.Latency;
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

                DS4State pState = device.getPreviousStateRef();
                //device.getPreviousState(PreviousState[ind]);
                //DS4State pState = PreviousState[ind];

                if (device.firstReport && device.isSynced())
                {
                    // Only send Log message when device is considered a primary device
                    if (device.PrimaryDevice)
                    {
                        deferredDiagnostics.FirstReport = true;
                        deferredDiagnostics.ProfileName = ProfilePath[ind];
                        deferredDiagnostics.InitialBattery = device.Battery;
                    }

                    device.firstReport = false;
                }

                CaptureReportBatteryDiagnostic(device, ref deferredDiagnostics);

                if (!device.PrimaryDevice)
                {
                    // Make sure a joined device is still linked
                    int jointInd = device.JointDeviceSlotNumber;
                    if (device.OutputMapGyro &&
                        jointInd != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                    {
                        // Output changes from Gyro data early. Seems better to ME... REE
                        GyroOutMode imuOutMode = Global.GetGyroOutMode(jointInd);
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
                                    GetReportOutputDevice(jointInd)?.ConvertandSendReport(tempMapState, jointInd);
                                }
                            }
                        }
                    }
                    else if (!device.OutputMapGyro)
                    {
                        // Copy for use in UDP
                        tempControlState.Motion = device.GetRawCurrentStateRef().Motion;
                    }

                    // Secondary sources still own their own diagnostics.
                    PublishReportDiagnostics(diagnosticsSource, ref deferredDiagnostics, cState);
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


                cState = Mapping.SetCurveAndDeadzone(ind, cState, TempState[ind], device);

                bool oscMonitoringPending = false;

                if (!recordingMacro && (useTempProfile[ind] ||
                    containsCustomAction(ind) || containsCustomExtras(ind) ||
                    getProfileActionCount(ind) > 0))
                {
                    DS4State tempMapState = MappedState[ind];
                    DS4State oscMapState = oscState[ind];

                    if (isUsingOSCSender())
                    {
                        tempMapState.CopyTo(oscMonitorPreviousState[ind]);
                        OSCPreMappingStep(cState, oscMapState);
                        cState.CopyTo(oscMonitorPendingState[ind]);
                        oscMonitoringPending = true;
                    }

                    Mapping.MapCustom(ind, cState, tempMapState, ExposedState[ind], touchPad[ind], this);
                    deferredDiagnostics.Switch2MouseMapperRan = true;

                    // Mapping owns controls; same-report physical metadata,
                    // touch, and motion remain observations from cState.
                    cState.CopyExtrasTo(tempMapState);

                    if (isUsingOSCServer())
                    {
                        OSCPostMappingStep(tempMapState, oscMapState);
                    }

                    cState = tempMapState;

                }
                else
                {
                    // No consumer in this report (for example, macro recording
                    // or a profile without mapping). Do not replay this report's
                    // deferred gyro/touch contribution when mapping resumes.
                    Mapping.DiscardPostMapStickData(ind);
                }

                if (!useDInputOnly[ind])
                {
                    // Perform this virtual trigger button check in post
                    if (activeOutDevType[ind].Normalize() == OutContType.ViiperDS4)
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

                    OutputDevice reportOutput = GetReportOutputDevice(ind);
                    reportOutput?.ConvertandSendReport(cState, ind);
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

                if (oscMonitoringPending)
                {
                    oscMonitoringWorker.Publish(ind,
                        oscMonitorPreviousState[ind],
                        oscMonitorPendingState[ind]);
                }

                // Output any synthetic events.
                Mapping.Commit(ind);

                // Update the Lightbar color
                DS4LightBar.updateLightBar(device, ind);

                if (device.PerformStateMerge)
                {
                    device.PreserveMergedStateData();
                }

                if (device.PerformStateMerge && !device.OutputMapGyro)
                {
                    // Copy for use in UDP
                    tempControlState.Motion = device.GetRawCurrentStateRef().Motion;
                }

                PublishReportDiagnostics(diagnosticsSource, ref deferredDiagnostics, cState);
            }
        }

        internal static void CaptureReportBatteryDiagnostic(DS4Device device,
            ref ReportDiagnosticsSnapshot snapshot)
        {
            if (!device.PrimaryDevice || Global.UseIconChoice != TrayIconChoice.Battery) return;
            snapshot.BatteryNotification = true;
            // The runtime's compatibility battery is authoritative. Switch 2
            // telemetry does not populate the legacy DS4State.Battery field.
            snapshot.Battery = device.Battery;
            snapshot.BatteryPolicyRevision = Global.TrayIconPolicyRevision;
        }

        private static void PublishReportDiagnostics(ReportDiagnosticsWorker.Source source,
            ref ReportDiagnosticsSnapshot snapshot, DS4State state)
        {
            source?.CaptureSwitch2Mouse(state, ref snapshot);
            if (source == null || !snapshot.HasWork) return;
            if (snapshot.StartupDiagnostic)
            {
                snapshot.Cross = state.Cross;
                snapshot.Circle = state.Circle;
                snapshot.PS = state.PS;
                snapshot.LX = state.LX;
                snapshot.LY = state.LY;
                snapshot.RX = state.RX;
                snapshot.RY = state.RY;
                snapshot.L2 = state.L2;
                snapshot.R2 = state.R2;
            }
            source.TryPublish(snapshot);
        }

        internal static void OSCPostMappingStep(DS4State tempMapState, DS4State oscMapState)
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

            if (oscMapState.LXAxis.ProfileCoordinate != 128) tempMapState.LXAxis = oscMapState.LXAxis;
            if (oscMapState.LYAxis.ProfileCoordinate != 128) tempMapState.LYAxis = oscMapState.LYAxis;
            tempMapState.L2 = oscMapState.L2 != 0 ? oscMapState.L2 : tempMapState.L2;
            if (oscMapState.RXAxis.ProfileCoordinate != 128) tempMapState.RXAxis = oscMapState.RXAxis;
            if (oscMapState.RYAxis.ProfileCoordinate != 128) tempMapState.RYAxis = oscMapState.RYAxis;
            tempMapState.R2 = oscMapState.R2 != 0 ? oscMapState.R2 : tempMapState.R2;
        }

        internal static void OSCPreMappingStep(DS4State cState,
            DS4State oscMapState)
        {
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

            if (oscMapState.LXAxis.ProfileCoordinate != 128) cState.LXAxis = oscMapState.LXAxis;
            if (oscMapState.LYAxis.ProfileCoordinate != 128) cState.LYAxis = oscMapState.LYAxis;
            cState.L2 = oscMapState.L2 != 0 ? oscMapState.L2 : cState.L2;
            if (oscMapState.RXAxis.ProfileCoordinate != 128) cState.RXAxis = oscMapState.RXAxis;
            if (oscMapState.RYAxis.ProfileCoordinate != 128) cState.RYAxis = oscMapState.RYAxis;
            cState.R2 = oscMapState.R2 != 0 ? oscMapState.R2 : cState.R2;
        }

        private void OSCMonitoringPostPublication(int index,
            DS4State previousState, DS4State currentState)
        {
            DS4State oscMapState = oscState[index];
            if (currentState.Battery != oscMapState.Battery)
            {
                oscSender.Send(new OscMessage(
                    "/ds4windows/monitor/" + index + "/battery",
                    Convert.ToInt32(currentState.Battery)));
                oscMapState.Battery = currentState.Battery;
            }
            CompareAndSendChangesToOSC(index, previousState, currentState);
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

        private void ProcessReportDiagnostics(
            ReportDiagnosticsSnapshot snapshot)
        {
            // An admitted historical log may complete during retirement, but
            // no queued observation is admitted for a replacement source.
            if (snapshot.Source?.IsCurrent != true ||
                !ReferenceEquals(DS4Controllers[snapshot.Controller], snapshot.Device)) return;
            if (!string.IsNullOrEmpty(snapshot.DeviceError))
            {
                LogDebug(snapshot.DeviceError);
            }

            if (snapshot.Switch2MouseDiagnostic)
            {
                var mouse = snapshot.Switch2Mouse;
                var raw = mouse;
                LogDebug($"[Switch2Mouse] slot={snapshot.Controller + 1} reports={mouse.Reports} " +
                    $"enabled={mouse.Enabled} source={mouse.Source} highRate={mouse.HighRate} mapperRan={mouse.CustomMapper}; " +
                    $"left present={raw.LeftPresent} changes={mouse.LeftChanges} xy={raw.LeftIrX},{raw.LeftIrY} " +
                    $"distance={raw.LeftIrDistance} roughness={raw.LeftIrRoughness} threshold={mouse.LeftThreshold}; " +
                    $"right present={raw.RightPresent} changes={mouse.RightChanges} xy={raw.RightIrX},{raw.RightIrY} " +
                    $"distance={raw.RightIrDistance} roughness={raw.RightIrRoughness} threshold={mouse.RightThreshold}; " +
                    $"gyro rawPeakL={mouse.LeftGyroPeak} rawPeakR={mouse.RightGyroPeak} " +
                    $"mode={mouse.GyroMode} mappedYaw={mouse.MappedYaw:F2} mappedPitch={mouse.MappedPitch:F2} " +
                    $"heldZL={mouse.ZLHeld} heldL={mouse.LHeld} heldR={mouse.RHeld}.");
            }

            if (snapshot.LagChanged)
            {
                if (snapshot.LagOn)
                {
                    LogDebug(string.Format(
                        DS4WinWPF.Properties.Resources.LatencyOverTen,
                        snapshot.Controller + 1, snapshot.Latency), true);
                }
                else
                {
                    LogDebug(DS4WinWPF.Properties.Resources.LatencyNotOverTen
                        .Replace("*number*",
                            (snapshot.Controller + 1).ToString()));
                }
            }

            if (snapshot.FirstReport)
            {
                bool profileExists = File.Exists(Path.Combine(appdatapath,
                    "Profiles", $"{snapshot.ProfileName}.xml"));
                string prolog = profileExists ?
                    string.Format(DS4WinWPF.Properties.Resources.UsingProfile,
                        (snapshot.Controller + 1).ToString(),
                        snapshot.ProfileName, $"{snapshot.InitialBattery}") :
                    string.Format(
                        DS4WinWPF.Properties.Resources.NotUsingProfile,
                        (snapshot.Controller + 1).ToString(),
                        $"{snapshot.InitialBattery}");
                LogDebug(prolog);
                AppLogger.LogToTray(prolog);
            }

            if (snapshot.BatteryNotification)
            {
                InvokeBatteryChanged((byte)snapshot.Battery, snapshot.Source);
            }

            if (snapshot.StartupDiagnostic)
            {
                StartupDiag($"On_Report deferred index={snapshot.Controller} " +
                    $"count={snapshot.StartupReportCount} " +
                    $"synced={snapshot.Synced} " +
                    $"latency={snapshot.StartupLatency} " +
                    $"useDInputOnly={snapshot.UseDInputOnly} " +
                    $"activeOut={snapshot.ActiveOutput} " +
                    $"buttons Cross={snapshot.Cross} Circle={snapshot.Circle} " +
                    $"PS={snapshot.PS} LX={snapshot.LX} LY={snapshot.LY} " +
                    $"RX={snapshot.RX} RY={snapshot.RY} " +
                    $"L2={snapshot.L2} R2={snapshot.R2}");
            }
        }

        private void ApplyLagFlashState(DS4Device device, int ind, bool on)
        {
            if (on)
            {
                lag[ind] = true;
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
                DS4LightBar.forcelight[ind] = false;
                DS4LightBar.forcedFlash[ind] = 0;
                device.LightBarColor = getMainColor(ind);
            }
        }

        private void LagFlashWarning(DS4Device device, int ind, bool on)
        {
            ApplyLagFlashState(device, ind, on);
            if (on)
            {
                LogDebug(string.Format(
                    DS4WinWPF.Properties.Resources.LatencyOverTen,
                    ind + 1, device.Latency), true);
            }
            else
            {
                LogDebug(DS4WinWPF.Properties.Resources.LatencyNotOverTen
                    .Replace("*number*", (ind + 1).ToString()));
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

        public bool[] touchreleased = new bool[MAX_DS4_CONTROLLER_COUNT] { true, true, true, true, true, true, true, true };

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
            if (ind < 0 || ind >= touchPad.Length || touchPad[ind] == null ||
                DS4Controllers[ind] == null)
            {
                return "none";
            }

            int direction = touchPad[ind].ConsumeProfileSwipeDirection();
            return direction < 0 ? "left" : direction > 0 ? "right" : "none";
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

        /// <summary>
        /// Production Switch 2 profile/output facet. Profile values are the
        /// user's persistent per-slot configuration and intentionally survive
        /// a controller lifetime. The returned inverse owns only resources and
        /// per-device presentation state created for this exact registration.
        /// </summary>
        private sealed class Switch2ControlServiceProfileStage :
            ISwitch2ControlServiceReversibleProfileStage,
            ISwitch2ControlServiceProfileStageDiagnostics
        {
            private readonly ControlService service;
            private string lastPrepareDiagnostic = "never-entered";

            internal Switch2ControlServiceProfileStage(ControlService service)
            {
                this.service = service ?? throw new ArgumentNullException(
                    nameof(service));
            }

            public Switch2ControlServiceReversibleStageResult TryPrepare(
                in Switch2ControlServiceProfileStageRequest request,
                out ISwitch2ControlServiceReversibleProfileStageInverse inverse)
            {
                lastPrepareDiagnostic = "entered";
                inverse = null;
                if (!Monitor.IsEntered(
                        service.switch2RuntimeRegistrationService.
                            LifecycleGate) ||
                    !request.IsValid || request.Slot < 0 ||
                    request.Slot >= service.DS4Controllers.Length ||
                    !ReferenceEquals(service.DS4Controllers[request.Slot],
                        request.Device) ||
                    service.touchPad[request.Slot] == null)
                {
                    return Switch2ControlServiceReversibleStageResult.Reject(
                        Switch2ControlServiceReversibleStageFailureKind.
                            InvalidCredential);
                }

                int slot = request.Slot;
                if (service.outputDevices[slot] != null ||
                    activeOutDevType[slot].Normalize() !=
                        OutContType.None || !useDInputOnly[slot])
                {
                    return Switch2ControlServiceReversibleStageResult.Reject(
                        Switch2ControlServiceReversibleStageFailureKind.
                            SlotOccupied);
                }

                var retained = new Switch2ControlServiceProfileStageInverse(
                    service, request, request.Device.LightBarColor,
                    useDInputOnly[slot], activeOutDevType[slot]);
                inverse = retained;
                try
                {
                    DS4Device device = request.Device;
                    // This transactional path does not run the legacy HID
                    // setup routine. Establish the same model-specific mapper
                    // inventory before a profile or report can consume it.
                    lastPrepareDiagnostic = "extra-button-registration";
                    Global.RefreshExtrasButtons(slot,
                        service.GetKnownExtraButtons(device));
                    bool useAutoProfile = useTempProfile[slot];
                    bool profileLoaded = useAutoProfile;
                    lastPrepareDiagnostic = "profile-selection";
                    if (!useAutoProfile)
                    {
                        if (device.isValidSerial() &&
                            containsLinkedProfile(device.getMacAddress()))
                        {
                            ProfilePath[slot] = getLinkedProfile(
                                device.getMacAddress());
                            Global.linkedProfileCheck[slot] = true;
                        }
                        else
                        {
                            ProfilePath[slot] = OlderProfilePath[slot];
                            Global.linkedProfileCheck[slot] = false;
                        }

                        lastPrepareDiagnostic = "profile-loading";
                        profileLoaded = LoadProfile(slot, false, service,
                            false, false);
                    }

                    if (profileLoaded)
                    {
                        lastPrepareDiagnostic = "profile-presentation";
                        device.LightBarColor = getMainColor(slot);
                        if (!getDInputOnly(slot) && device.isSynced())
                        {
                            lastPrepareDiagnostic = "output-plug";
                            service.PluginOutDev(slot, device);
                        }
                        else
                        {
                            useDInputOnly[slot] = true;
                            activeOutDevType[slot] = OutContType.None;
                        }

                        lastPrepareDiagnostic = "device-post-setup";
                        device.setIdleTimeout(getIdleDisconnectTimeout(slot));
                        device.setBTPollRate(getBTPollRate(slot));
                        service.touchPad[slot].ResetTrackAccel(
                            getTrackballFriction(slot));
                        service.touchPad[slot].ResetToggleGyroModes();
                        service.touchPad[slot].PostSetup();
                        device.RumbleAutostopTime = getRumbleAutostopTime(slot);
                        device.setRumble(0, 0);
                    }

                    lastPrepareDiagnostic = "output-validation";
                    retained.PreparedOutput = service.outputDevices[slot];
                    bool virtualRequired = profileLoaded &&
                        !getDInputOnly(slot);
                    if (!profileLoaded || virtualRequired &&
                        (retained.PreparedOutput == null ||
                            useDInputOnly[slot]))
                    {
                        return Switch2ControlServiceReversibleStageResult.
                            Uncertain(
                                Switch2ControlServiceReversibleStageFailureKind.
                                    ProfileSetupRejected);
                    }

                    retained.Prepared = true;
                    lastPrepareDiagnostic = "succeeded";
                    return Switch2ControlServiceReversibleStageResult.Success();
                }
                catch (Exception exception)
                {
                    lastPrepareDiagnostic += $":threw:" +
                        exception.GetType().FullName;
                    retained.PreparedOutput = service.outputDevices[slot];
                    return Switch2ControlServiceReversibleStageResult.Uncertain(
                        Switch2ControlServiceReversibleStageFailureKind.
                            DependencyThrew);
                }
            }

            string ISwitch2ControlServiceProfileStageDiagnostics.
                LastPrepareDiagnostic => lastPrepareDiagnostic;
        }

        private sealed class Switch2ControlServiceProfileStageInverse :
            ISwitch2ControlServiceReversibleProfileStageInverse
        {
            private readonly ControlService service;
            private readonly InputControllerSlotToken token;
            private readonly DS4Device device;
            private readonly int slot;
            private readonly DS4Color previousLightBarColor;
            private readonly bool previousUseDInputOnly;
            private readonly OutContType previousActiveOutput;
            private readonly List<DS4Controls> previousExtraButtons;
            private bool consumed;
            private bool outputChangeActive;
            private bool outputChangeUncertain;
            private bool cleanupWarningReported;
            private bool undoActive;
            private int outputChangeThread;
            private OutputDevice outputBeforeChange;
            private OutputDevice uncertainProducedOutput;

            internal Switch2ControlServiceProfileStageInverse(
                ControlService service,
                in Switch2ControlServiceProfileStageRequest request,
                DS4Color previousLightBarColor, bool previousUseDInputOnly,
                OutContType previousActiveOutput)
            {
                this.service = service;
                token = request.Token;
                device = request.Device;
                slot = request.Slot;
                this.previousLightBarColor = previousLightBarColor;
                this.previousUseDInputOnly = previousUseDInputOnly;
                this.previousActiveOutput = previousActiveOutput;
                previousExtraButtons = Global.GetControlSettingsGroup(slot)
                    .ExtraDeviceButtons.Select(setting => setting.control)
                    .ToList();
                if (!Monitor.IsEntered(service.switch2RuntimeRegistrationService.LifecycleGate) ||
                    !ReferenceEquals(service.DS4Controllers[slot], device) ||
                    !service.inputRegistrationTable.TryAuthenticateBoundExternalStage(token, out _))
                    throw new InvalidOperationException("Switch 2 profile output ownership requires an exact staged input lifetime.");
                service.switch2ProfileOutputOwners ??=
                    new Switch2ControlServiceProfileStageInverse[service.DS4Controllers.Length];
                if (service.switch2ProfileOutputOwners[slot] != null)
                    throw new InvalidOperationException("Switch 2 profile output ownership is already retained for this slot.");
                service.switch2ProfileOutputOwners[slot] = this;
            }

            internal OutputDevice PreparedOutput { get; set; }

            internal bool Prepared { get; set; }

            private bool IsCurrentOutputOwner(bool completing = false)
            {
                if (consumed || !ReferenceEquals(service.switch2ProfileOutputOwners?[slot], this) ||
                    !ReferenceEquals(service.DS4Controllers[slot], device)) return false;
                InputControllerSlotSnapshot snapshot = service.inputRegistrationTable.GetSnapshot()[slot];
                return snapshot.Token == token &&
                    (snapshot.State is InputControllerSlotState.Bound or InputControllerSlotState.Attached ||
                        snapshot.State == InputControllerSlotState.Retiring &&
                            (completing && outputChangeActive ||
                                snapshot.ActionActive && device is Switch2RuntimeInputDevice runtime &&
                                runtime.IsCurrentVirtualOutputTransitionThread) ||
                        undoActive && snapshot.State == InputControllerSlotState.Quiesced);
            }

            internal bool TryBeginOutputChange(DS4Device source)
            {
                if (!ReferenceEquals(source, device) || outputChangeActive || outputChangeUncertain ||
                    !IsCurrentOutputOwner() ||
                    !ReferenceEquals(service.outputDevices[slot], PreparedOutput)) return false;
                outputChangeActive = true;
                outputChangeThread = Environment.CurrentManagedThreadId;
                outputBeforeChange = PreparedOutput;
                return true;
            }

            internal void CompleteOutputChange(OutputDevice produced,
                bool allowUnpublishedNull, bool operationSucceeded)
            {
                lock (service.switch2RuntimeRegistrationService.LifecycleGate)
                {
                    if (!outputChangeActive || outputChangeThread != Environment.CurrentManagedThreadId)
                        throw new InvalidOperationException("Switch 2 output ownership completion has no matching operation.");
                    try
                    {
                        OutputDevice current = service.outputDevices[slot];
                        if (!operationSucceeded)
                        {
                            // Preserve the prior exact output even though the
                            // legacy array may already be null. This generation
                            // remains quarantinable; a retry cannot invent proof.
                            outputChangeUncertain = true;
                            return;
                        }
                        if (!IsCurrentOutputOwner(completing: true) ||
                            !ReferenceEquals(PreparedOutput, outputBeforeChange) ||
                            !(ReferenceEquals(current, produced) ||
                                allowUnpublishedNull && current == null && outputBeforeChange == null))
                        {
                            // Connect may have completed before publication
                            // threw. Retain the exact candidate, but do not
                            // invent a successful detach from an empty array.
                            uncertainProducedOutput ??= produced;
                            outputChangeUncertain = true;
                            throw new InvalidOperationException("Switch 2 output ownership changed during the output operation.");
                        }
                        PreparedOutput = current;
                    }
                    finally
                    {
                        outputChangeActive = false;
                        outputChangeThread = 0;
                        outputBeforeChange = null;
                    }
                }
            }

            public bool Authenticates(
                in Switch2ControlServiceProfileStageRequest request) =>
                !consumed && request.IsValid && request.Token == token &&
                request.Slot == slot && ReferenceEquals(request.Device,
                    device);

            public Switch2ControlServiceReversibleStageResult TryUndo(
                in Switch2ControlServiceProfileStageRequest request)
            {
                if (!Monitor.IsEntered(
                        service.switch2RuntimeRegistrationService.
                            LifecycleGate) ||
                    !Authenticates(request) ||
                    !ReferenceEquals(service.DS4Controllers[slot], device))
                {
                    return Switch2ControlServiceReversibleStageResult.Reject(
                        Switch2ControlServiceReversibleStageFailureKind.
                            InvalidCredential);
                }

                try
                {
                    OutputDevice current = service.outputDevices[slot];
                    if (outputChangeActive || outputChangeUncertain ||
                        !ReferenceEquals(service.switch2ProfileOutputOwners?[slot], this) ||
                        !ReferenceEquals(current, PreparedOutput))
                    {
                        ReportUnprovenCleanupOnce(
                            Switch2ControlServiceReversibleStageFailureKind.SlotChanged);
                        return Switch2ControlServiceReversibleStageResult.
                            Uncertain(
                                Switch2ControlServiceReversibleStageFailureKind.
                                    SlotChanged);
                    }
                    if (current != null)
                    {
                        // Every admitted output replacement transferred this
                        // retained proof to its exact synchronous result.
                        useDInputOnly[slot] = false;
                        undoActive = true;
                        try
                        {
                            service.UnplugOutDev(slot, device, immediate: true,
                                force: true);
                        }
                        finally { undoActive = false; }
                    }

                    service.outputDevices[slot] = null;
                    useDInputOnly[slot] = previousUseDInputOnly;
                    activeOutDevType[slot] = previousActiveOutput;
                    device.LightBarColor = previousLightBarColor;
                    service.touchPad[slot]?.Reset();
                    // Restore the retained slot inventory only after this
                    // exact runtime has emitted terminal neutral and stopped.
                    Global.RefreshExtrasButtons(slot, previousExtraButtons);
                    consumed = true;
                    service.switch2ProfileOutputOwners[slot] = null;
                    return Switch2ControlServiceReversibleStageResult.Success();
                }
                catch
                {
                    ReportUnprovenCleanupOnce(
                        Switch2ControlServiceReversibleStageFailureKind.CleanupRejected);
                    return Switch2ControlServiceReversibleStageResult.Uncertain(
                        Switch2ControlServiceReversibleStageFailureKind.
                            CleanupRejected);
                }
            }

            private void ReportUnprovenCleanupOnce(
                Switch2ControlServiceReversibleStageFailureKind failure)
            {
                if (cleanupWarningReported) return;
                cleanupWarningReported = true;
                try
                {
                    service.LogDebug($"Switch 2 input slot #{slot + 1} cleanup could not prove exact virtual-output retirement ({failure}). The physical slot is retained pending cleanup proof.", warning: true);
                }
                catch
                {
                    // A diagnostic observer cannot alter cleanup authority.
                }
            }
        }
    }
}
