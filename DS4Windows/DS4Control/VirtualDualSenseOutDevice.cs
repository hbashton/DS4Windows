/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows
{
    public class VirtualDualSenseOutDevice : OutputDevice
    {
        public const string DevType = "DualSense";

        private readonly VirtualDualSenseDriverClient driverClient;
        private bool submitFailureLogged;

        public VirtualDualSenseOutDevice()
        {
            driverClient = new VirtualDualSenseDriverClient();
        }

        public override void Connect()
        {
            driverClient.Connect();
            connected = true;
        }

        public override void Disconnect()
        {
            connected = false;
            driverClient.Disconnect();
        }

        public override void ConvertandSendReport(DS4State state, int device)
        {
            _ = device;
            if (!connected)
            {
                return;
            }

            try
            {
                driverClient.SubmitInputReport(VirtualDualSenseInputReport.BuildUsbReport(state));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                LogSubmitFailure(ex.Message);
            }
            catch (System.IO.IOException ex)
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
                catch
                {
                    connected = false;
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

        private void LogSubmitFailure(string message)
        {
            connected = false;
            driverClient.Disconnect();

            if (submitFailureLogged)
            {
                return;
            }

            submitFailureLogged = true;
            AppLogger.LogToGui($"Virtual DualSense output stopped: {message}", true);
        }
    }
}
