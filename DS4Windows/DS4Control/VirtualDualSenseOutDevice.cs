/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace DS4Windows
{
    public class VirtualDualSenseOutDevice : OutputDevice
    {
        public const string DevType = "DualSense";
        private const int OutputReportPollPeriodMs = 15;
        private const byte UsbOutputReportId = 0x02;
        private const int MinUsbOutputReportLength = 48;

        private readonly VirtualDualSenseDriverClient driverClient;
        private Timer outputReportPollTimer;
        private int outputPollInProgress;
        private int lastInputDeviceIndex = -1;
        private uint lastOutputReportSequence;
        private int submitFailureLogged;

        public VirtualDualSenseOutDevice()
        {
            driverClient = new VirtualDualSenseDriverClient();
        }

        public override void Connect()
        {
            driverClient.Connect();
            Volatile.Write(ref submitFailureLogged, 0);
            Volatile.Write(ref lastInputDeviceIndex, -1);
            connected = true;
            StartOutputReportPolling();
        }

        public override void Disconnect()
        {
            connected = false;
            StopOutputReportPolling();
            driverClient.Disconnect();
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
                driverClient.SubmitInputReport(VirtualDualSenseInputReport.BuildUsbReport(state));
            }
            catch (Win32Exception ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
        }

        public override void ResetState(bool submit = true)
        {
            if (submit && connected)
            {
                try
                {
                    driverClient.SubmitInputReport(VirtualDualSenseInputReport.BuildUsbReport(new DS4State()));
                }
                catch (Win32Exception ex)
                {
                    LogSubmitFailure(ex.Message);
                }
                catch (IOException ex)
                {
                    LogSubmitFailure(ex.Message);
                }
                catch (ObjectDisposedException ex)
                {
                    LogSubmitFailure(ex.Message);
                }
            }
        }

        public override string GetDeviceType() => DevType;

        public override void RemoveFeedbacks()
        {
        }

        public override void RemoveFeedback(int inIdx)
        {
            _ = inIdx;
        }

        private void StartOutputReportPolling()
        {
            StopOutputReportPolling();
            lastOutputReportSequence = 0;
            outputPollInProgress = 0;
            outputReportPollTimer = new Timer(PollOutputReports, null,
                OutputReportPollPeriodMs, OutputReportPollPeriodMs);
        }

        private void StopOutputReportPolling()
        {
            Timer timer = Interlocked.Exchange(ref outputReportPollTimer, null);
            timer?.Dispose();
        }

        private void PollOutputReports(object state)
        {
            _ = state;
            if (!connected || Interlocked.Exchange(ref outputPollInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                if (!driverClient.TryReadOutputReport(out byte[] report, out uint sequence) ||
                    sequence == lastOutputReportSequence)
                {
                    return;
                }

                lastOutputReportSequence = sequence;
                if (!IsUsbOutputReport02(report))
                {
                    return;
                }

                ApplyOutputReportToPhysicalController(report);
            }
            catch (Win32Exception ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (IOException ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (ObjectDisposedException)
            {
                connected = false;
                StopOutputReportPolling();
            }
            finally
            {
                Volatile.Write(ref outputPollInProgress, 0);
            }
        }

        private static bool IsUsbOutputReport02(byte[] report)
        {
            return report != null &&
                report.Length >= MinUsbOutputReportLength &&
                report[0] == UsbOutputReportId;
        }

        private void ApplyOutputReportToPhysicalController(byte[] report)
        {
            int deviceIndex = Volatile.Read(ref lastInputDeviceIndex);
            if (deviceIndex < 0 ||
                Program.rootHub == null ||
                deviceIndex >= Program.rootHub.DS4Controllers.Length)
            {
                return;
            }

            if (Program.rootHub.DS4Controllers[deviceIndex] is InputDevices.DualSenseDevice dualSense)
            {
                dualSense.ApplyVirtualDualSenseUsbOutputReport(report);
            }
        }

        private void LogSubmitFailure(string message)
        {
            connected = false;
            StopOutputReportPolling();
            driverClient.Disconnect();

            if (Interlocked.Exchange(ref submitFailureLogged, 1) == 1)
            {
                return;
            }

            AppLogger.LogToGui($"Virtual DualSense output stopped: {message}", true);
        }
    }
}
