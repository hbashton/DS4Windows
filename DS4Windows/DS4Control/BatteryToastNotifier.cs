/*
DS4Windows
Copyright (C) 2026 hbashton

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
using System.Collections.Generic;
using System.Linq;

namespace DS4Windows
{
    internal sealed class BatteryToastNotifier
    {
        public static readonly int[] DefaultThresholds = { 10, 25, 50, 75 };

        private const int ResetHysteresis = 3;
        private readonly int[] thresholds;
        private readonly HashSet<int>[] notifiedThresholds = new HashSet<int>[Global.MAX_DS4_CONTROLLER_COUNT];
        private readonly int?[] lastBattery = new int?[Global.MAX_DS4_CONTROLLER_COUNT];

        public BatteryToastNotifier(IEnumerable<int> thresholds = null)
        {
            this.thresholds = (thresholds ?? DefaultThresholds)
                .Where(threshold => threshold > 0 && threshold <= 100)
                .Distinct()
                .OrderBy(threshold => threshold)
                .ToArray();

            for (int i = 0; i < notifiedThresholds.Length; i++)
            {
                notifiedThresholds[i] = new HashSet<int>();
            }
        }

        public void Update(DS4Device device)
        {
            if (device == null)
            {
                return;
            }

            Update(device.DeviceSlotNumber, device.DisplayName, device.getBattery(), device.isCharging());
        }

        public void Update(int controllerIndex, string controllerName, int batteryPercent, bool charging)
        {
            if (!IsValidControllerIndex(controllerIndex))
            {
                return;
            }

            if (charging || batteryPercent <= 0 || batteryPercent > 100)
            {
                Reset(controllerIndex);
                lastBattery[controllerIndex] = batteryPercent;
                return;
            }

            ResetRecoveredThresholds(controllerIndex, batteryPercent);

            if (!Global.BatteryToastNotifications || Global.Notifications == 0)
            {
                lastBattery[controllerIndex] = batteryPercent;
                return;
            }

            int? previousBattery = lastBattery[controllerIndex];
            int threshold = FindCrossedThreshold(controllerIndex, previousBattery, batteryPercent);
            lastBattery[controllerIndex] = batteryPercent;

            if (threshold == 0)
            {
                return;
            }

            notifiedThresholds[controllerIndex].Add(threshold);
            string displayName = string.IsNullOrWhiteSpace(controllerName) ? "Controller" : controllerName.Trim();
            string message = $"Controller {controllerIndex + 1} - {displayName}{Environment.NewLine}Battery {batteryPercent}%";
            AppLogger.LogToTray(message, true, true);
        }

        public void Reset(int controllerIndex)
        {
            if (!IsValidControllerIndex(controllerIndex))
            {
                return;
            }

            notifiedThresholds[controllerIndex].Clear();
            lastBattery[controllerIndex] = null;
        }

        private int FindCrossedThreshold(int controllerIndex, int? previousBattery, int batteryPercent)
        {
            foreach (int threshold in thresholds)
            {
                if (notifiedThresholds[controllerIndex].Contains(threshold))
                {
                    continue;
                }

                if (batteryPercent <= threshold &&
                    (!previousBattery.HasValue || previousBattery.Value > threshold))
                {
                    return threshold;
                }
            }

            return 0;
        }

        private void ResetRecoveredThresholds(int controllerIndex, int batteryPercent)
        {
            if (notifiedThresholds[controllerIndex].Count == 0)
            {
                return;
            }

            foreach (int threshold in thresholds)
            {
                if (batteryPercent >= threshold + ResetHysteresis)
                {
                    notifiedThresholds[controllerIndex].Remove(threshold);
                }
            }
        }

        private static bool IsValidControllerIndex(int controllerIndex)
        {
            return controllerIndex >= 0 && controllerIndex < Global.MAX_DS4_CONTROLLER_COUNT;
        }
    }
}
