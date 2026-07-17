/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    public enum ViiperDebugTest
    {
        Prerequisites,
        Xbox360,
        DualShock4,
        DualSense,
        DualSenseEdge,
        Switch2Pro,
        AdaptiveTriggers,
        HapticsTone,
        All,
    }

    public sealed class ViiperBackendDebugger
    {
        private readonly Action<string> logSink;

        public ViiperBackendDebugger(Action<string> logSink = null)
        {
            this.logSink = logSink;
        }

        public Task RunAsync(ViiperDebugTest test, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Run(test, cancellationToken), cancellationToken);
        }

        public Task StartDualSenseTrafficCaptureAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => SetDualSenseTrafficCapture(true, true, cancellationToken), cancellationToken);
        }

        public Task StopDualSenseTrafficCaptureAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => SetDualSenseTrafficCapture(false, false, cancellationToken), cancellationToken);
        }

        public Task ClearDualSenseTrafficCaptureAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
                if (!status.Ready)
                {
                    throw new InvalidOperationException($"VIIPER backend is not ready: {status.DisplayText}");
                }

                ViiperClient client = new ViiperClient(ViiperSetupManager.ApiHost, ViiperSetupManager.ApiPort);
                string response = client.ClearDualSenseTrafficCapture();
                Log($"DualSense traffic capture cleared: {FormatJson(response)}");
            }, cancellationToken);
        }

        public Task DumpDualSenseTrafficCaptureAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
                if (!status.Ready)
                {
                    throw new InvalidOperationException($"VIIPER backend is not ready: {status.DisplayText}");
                }

                ViiperClient client = new ViiperClient(ViiperSetupManager.ApiHost, ViiperSetupManager.ApiPort);
                string response = client.GetDualSenseTrafficCapture();
                string dumpPath = WriteDualSenseTrafficDump(response);
                Log($"DualSense traffic capture exported to {dumpPath}");
                Log($"DualSense traffic capture summary: {FormatCaptureSummary(response)}");
            }, cancellationToken);
        }

        private void SetDualSenseTrafficCapture(bool enabled, bool clear, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            if (!status.Ready)
            {
                throw new InvalidOperationException($"VIIPER backend is not ready: {status.DisplayText}");
            }

            ViiperClient client = new ViiperClient(ViiperSetupManager.ApiHost, ViiperSetupManager.ApiPort);
            string response = client.SetDualSenseTrafficCapture(enabled, clear);
            Log($"DualSense traffic capture enabled={enabled} clear={clear}: {FormatJson(response)}");
        }

        private static string WriteDualSenseTrafficDump(string response)
        {
            string basePath = string.IsNullOrWhiteSpace(Global.appdatapath) ?
                Global.appDataPpath : Global.appdatapath;
            string logPath = Path.Combine(basePath, "Logs");
            Directory.CreateDirectory(logPath);

            string filePath = Path.Combine(logPath, $"dualsense_traffic_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(filePath, FormatJson(response));
            return filePath;
        }

        private static string FormatCaptureSummary(string response)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(response);
                JsonElement root = document.RootElement;
                bool enabled = root.TryGetProperty("enabled", out JsonElement enabledElement) &&
                    enabledElement.GetBoolean();
                int count = root.TryGetProperty("count", out JsonElement countElement) &&
                    countElement.TryGetInt32(out int parsedCount) ? parsedCount : 0;
                return $"enabled={enabled} count={count}";
            }
            catch
            {
                return "Unable to parse capture summary.";
            }
        }

        private void Run(ViiperDebugTest test, CancellationToken cancellationToken)
        {
            Log("============================================================");
            Log($"VIIPER DEBUG SESSION START test={test} utc={DateTime.UtcNow:O}");
            Log($"DS4Windows exe={Global.exelocation}");
            Log($"Verbose logging={Global.VerboseStartupLogging}");

            if (!Global.VerboseStartupLogging)
            {
                Log("WARNING: VIIPER debugger was run while verbose logging is disabled. Turn on Settings > Verbose logging for full diagnostic context.");
            }

            Stopwatch total = Stopwatch.StartNew();
            try
            {
                if (test == ViiperDebugTest.All || test == ViiperDebugTest.Prerequisites)
                {
                    RunStep("Prerequisites", RunPrerequisiteProbe, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.Xbox360)
                {
                    RunDeviceProbe(ViiperVirtualDeviceType.Xbox360, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.DualShock4)
                {
                    RunDeviceProbe(ViiperVirtualDeviceType.DualShock4, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.DualSense)
                {
                    RunDeviceProbe(ViiperVirtualDeviceType.DualSense, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.DualSenseEdge)
                {
                    RunDeviceProbe(ViiperVirtualDeviceType.DualSenseEdge, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.Switch2Pro)
                {
                    RunDeviceProbe(ViiperVirtualDeviceType.Switch2Pro, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.AdaptiveTriggers)
                {
                    RunStep("Adaptive trigger emulation", RunAdaptiveTriggerProbe, cancellationToken);
                }

                if (test == ViiperDebugTest.All || test == ViiperDebugTest.HapticsTone)
                {
                    RunStep("DualSense Bluetooth haptics tone", RunHapticsToneProbe, cancellationToken);
                }
            }
            finally
            {
                total.Stop();
                Log($"VIIPER DEBUG SESSION END test={test} elapsedMs={total.ElapsedMilliseconds}");
                Log("============================================================");
            }
        }

        private void RunPrerequisiteProbe()
        {
            ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
            Log($"Prerequisite status ready={status.Ready} display='{status.DisplayText}'");
            Log($"VIIPER installed={status.ViiperInstalled} path='{status.ViiperPath}'");
            Log($"usbip-win2 installed={status.UsbipInstalled}");
            Log($"VIIPER server running={status.ServerRunning} endpoint={ViiperSetupManager.ApiHost}:{ViiperSetupManager.ApiPort}");
            Log($"Bundled setup script found={status.SetupScriptFound} path='{status.SetupScriptPath}'");
        }

        private void RunDeviceProbe(ViiperVirtualDeviceType type, CancellationToken cancellationToken)
        {
            RunStep($"{type} virtual output", () =>
            {
                ViiperPrerequisiteStatus status = ViiperSetupManager.GetStatus(tryStartServer: true);
                Log($"Device={type} statusBeforeCreate ready={status.Ready} display='{status.DisplayText}'");
                if (!status.Ready)
                {
                    throw new InvalidOperationException($"VIIPER backend is not ready: {status.DisplayText}");
                }

                ViiperClient client = new ViiperClient(ViiperSetupManager.ApiHost, ViiperSetupManager.ApiPort);
                string viiperDeviceName = ViiperStatePacketBuilder.GetViiperDeviceName(type);
                int packetLength = ViiperStatePacketBuilder.BuildNeutral(type).Length;
                int feedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(type);
                Log($"Device={type} viiperName={viiperDeviceName} packetLength={packetLength} feedbackLength={feedbackLength}");

                using ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(type);
                Log($"Device={type} create/open stream OK");

                WritePacket(stream, type, "neutral", ViiperStatePacketBuilder.CreateNeutralState(), cancellationToken);
                WritePacket(stream, type, "buttons", BuildButtonState(type), cancellationToken);
                WritePacket(stream, type, "axes", BuildAxisState(type), cancellationToken);
                WritePacket(stream, type, "touch", BuildTouchState(), cancellationToken);
                WritePacket(stream, type, "reset", ViiperStatePacketBuilder.CreateNeutralState(), cancellationToken);
                Log($"Device={type} dispose temp stream begin");
            }, cancellationToken);
        }

        private void WritePacket(ViiperDeviceStream stream, ViiperVirtualDeviceType type, string label, DS4State state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] packet = ViiperStatePacketBuilder.Build(type, state, -1);
            Stopwatch stopwatch = Stopwatch.StartNew();
            stream.Write(packet);
            stopwatch.Stop();
            Log($"Device={type} packet={label} bytes={packet.Length} writeMs={stopwatch.ElapsedMilliseconds} hex={ToHexPreview(packet)}");
            Thread.Sleep(35);
        }

        private void RunAdaptiveTriggerProbe()
        {
            Log("VIIPER adaptive trigger passthrough is enabled for physical DualSense/DualSense Edge input controllers.");
            Log("Expected feedback contract: extended DualSense streams send base rumble/LED bytes followed by R2[8] then L2[8] raw trigger effect bytes parsed from USB output report 0x02.");

            bool anyApplied = false;
            List<int> appliedControllers = new List<int>();
            try
            {
                if (Program.rootHub != null)
                {
                    for (int i = 0; i < Program.rootHub.DS4Controllers.Length; i++)
                    {
                        if (Program.rootHub.DS4Controllers[i] == null)
                        {
                            continue;
                        }

                        bool right = ViiperOutDevice.ApplySyntheticDualSenseTriggerFeedback(i, true,
                            0x21, 0xFC, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0x00);
                        bool left = ViiperOutDevice.ApplySyntheticDualSenseTriggerFeedback(i, false,
                            0x21, 0xFC, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0x00);
                        if (right || left)
                        {
                            anyApplied = true;
                            appliedControllers.Add(i);
                            Log($"Synthetic game-feedback trigger effect applied to controller {i + 1}: right={right} left={left}");
                        }
                    }
                }

                if (anyApplied)
                {
                    Thread.Sleep(1200);
                }
            }
            finally
            {
                foreach (int controllerIndex in appliedControllers)
                {
                    bool rightReset = ViiperOutDevice.ResetSyntheticDualSenseTriggerFeedback(controllerIndex, true);
                    bool leftReset = ViiperOutDevice.ResetSyntheticDualSenseTriggerFeedback(controllerIndex, false);
                    Log($"Synthetic game-feedback trigger effect reset on controller {controllerIndex + 1}: right={rightReset} left={leftReset}");
                }
            }

            if (!anyApplied)
            {
                Log("No physical DualSense/DualSense Edge input controller was available for synthetic trigger feedback.");
            }
        }

        private void RunHapticsToneProbe()
        {
            Log("Sending SAxense-style Bluetooth HID report 0x32 test tone to physical Bluetooth DualSense controllers.");
            bool anyApplied = false;
            if (Program.rootHub != null)
            {
                for (int i = 0; i < Program.rootHub.DS4Controllers.Length; i++)
                {
                    if (Program.rootHub.DS4Controllers[i] == null)
                    {
                        continue;
                    }

                    bool applied = ViiperOutDevice.PlaySyntheticDualSenseHapticsTone(i);
                    string status = Program.rootHub.DS4Controllers[i] is DualSenseDevice dualSenseDevice ?
                        dualSenseDevice.LastBluetoothHapticsWriteStatus :
                        "Skipped: controller is not a DualSense input device.";
                    Log($"Bluetooth haptics tone controller {i + 1}: applied={applied} status=\"{status}\"");
                    anyApplied |= applied;
                }
            }

            if (!anyApplied)
            {
                Log("No Bluetooth DualSense/DualSense Edge input controller accepted the haptics tone.");
            }
        }


        private void RunStep(string name, Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch stopwatch = Stopwatch.StartNew();
            Log($"[BEGIN] {name}");
            try
            {
                action();
                stopwatch.Stop();
                Log($"[PASS] {name} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log($"[FAIL] {name} elapsedMs={stopwatch.ElapsedMilliseconds}");
                LogException(name, ex);
            }
        }

        private static DS4State BuildButtonState(ViiperVirtualDeviceType type)
        {
            DS4State state = new DS4State
            {
                Cross = true,
                Circle = true,
                Square = true,
                Triangle = true,
                L1 = true,
                R1 = true,
                L2 = 255,
                R2 = 255,
                L2Btn = true,
                R2Btn = true,
                Share = true,
                Options = true,
                PS = true,
                DpadUp = true,
                DpadRight = true,
            };

            if (type == ViiperVirtualDeviceType.DualSense ||
                type == ViiperVirtualDeviceType.DualSenseEdge ||
                type == ViiperVirtualDeviceType.Switch2Pro)
            {
                state.Mute = true;
                state.Capture = true;
            }

            if (type == ViiperVirtualDeviceType.DualSenseEdge ||
                type == ViiperVirtualDeviceType.Switch2Pro)
            {
                state.FnL = true;
                state.FnR = true;
                state.BLP = true;
                state.BRP = true;
                state.SideL = true;
                state.SideR = true;
            }

            return state;
        }

        private static DS4State BuildAxisState(ViiperVirtualDeviceType type)
        {
            _ = type;
            return new DS4State
            {
                LX = 255,
                LY = 0,
                RX = 32,
                RY = 224,
                L2 = 128,
                R2 = 192,
            };
        }

        private static DS4State BuildTouchState()
        {
            DS4State state = new DS4State
            {
                OutputTouchButton = true,
                TouchButton = true,
            };

            state.TrackPadTouch0.X = 320;
            state.TrackPadTouch0.Y = 240;
            state.TrackPadTouch0.IsActive = true;
            state.TrackPadTouch1.X = 1500;
            state.TrackPadTouch1.Y = 760;
            state.TrackPadTouch1.IsActive = true;
            return state;
        }

        private static string ToHexPreview(byte[] data)
        {
            int count = Math.Min(data.Length, 32);
            string[] parts = new string[count];
            for (int i = 0; i < count; i++)
            {
                parts[i] = data[i].ToString("X2");
            }

            return data.Length > count ? string.Join(" ", parts) + " ..." : string.Join(" ", parts);
        }

        private void LogException(string step, Exception ex)
        {
            Log($"EXCEPTION step={step} type={ex.GetType().FullName} message={ex.Message}");
            Log(ex.ToString());
        }

        private static string FormatJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
            }
            catch
            {
                return json;
            }
        }

        private void Log(string message)
        {
            string line = $"VIIPER DEBUG: {message}";
            AppLogger.LogToGui(line, false);
            logSink?.Invoke(line);
        }
    }
}
