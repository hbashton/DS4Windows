/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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
                int packetLength = ViiperStatePacketBuilder.Build(type, new DS4State(), -1).Length;
                int feedbackLength = ViiperStatePacketBuilder.GetFeedbackLength(type);
                Log($"Device={type} viiperName={viiperDeviceName} packetLength={packetLength} feedbackLength={feedbackLength}");

                using ViiperDeviceStream stream = client.CreateDeviceAndOpenStream(type);
                Log($"Device={type} create/open stream OK");

                WritePacket(stream, type, "neutral", new DS4State(), cancellationToken);
                WritePacket(stream, type, "buttons", BuildButtonState(type), cancellationToken);
                WritePacket(stream, type, "axes", BuildAxisState(type), cancellationToken);
                WritePacket(stream, type, "touch", BuildTouchState(), cancellationToken);
                WritePacket(stream, type, "reset", new DS4State(), cancellationToken);
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
            Log("Expected feedback contract: DualSense feedback may extend the base 6-byte rumble/LED packet with R2[8] then L2[8] raw trigger effect bytes.");
            Log("If trigger effects do not reach the real controller, update VIIPER to expose the trigger blocks parsed from USB output report 0x02.");
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

        private void Log(string message)
        {
            string line = $"VIIPER DEBUG: {message}";
            AppLogger.LogToGui(line, false);
            logSink?.Invoke(line);
        }
    }
}
