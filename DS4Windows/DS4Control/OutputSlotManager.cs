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
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DS4WinWPF.DS4Control;

namespace DS4Windows
{
    public class OutputSlotManager
    {
        public const int DELAY_TIME = 500; // measured in ms
        private OutSlotDevice[] outputSlots;/* = new OutSlotDevice[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            new OutSlotDevice(0), new OutSlotDevice(1),
            new OutSlotDevice(2), new OutSlotDevice(3)
        };
        */
        private int lastSlotIndex;

        public int NumAttachedDevices
        {
            get
            {
                int result = 0;
                for (int i = 0; i < outputSlots.Length; i++)
                {
                    OutSlotDevice tmp = outputSlots[i];
                    if (tmp.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached)
                    {
                        result++;
                    }
                }

                return result;
            }
        }

        private Dictionary<int, OutputDevice> deviceDict = new Dictionary<int, OutputDevice>();
        private Dictionary<OutputDevice, int> revDeviceDict = new Dictionary<OutputDevice, int>();
        private OutputDevice[] outputDevices = new OutputDevice[ControlService.CURRENT_DS4_CONTROLLER_LIMIT];

        private int queuedTasks = 0;
        private ReaderWriterLockSlim queueLocker;
        private readonly Action<IReadOnlyCollection<string>>
            virtualSonyRegisteredCallback;

        public bool RunningQueue { get => queuedTasks > 0; }
        public OutSlotDevice[] OutputSlots { get => outputSlots; }

        public delegate void SlotAssignedDelegate(OutputSlotManager sender,
            int slotNum, OutSlotDevice outSlotDev);
        public event SlotAssignedDelegate SlotAssigned;

        public delegate void SlotUnassignedDelegate(OutputSlotManager sender,
            int slotNum, OutSlotDevice outSlotDev);
        public event SlotUnassignedDelegate SlotUnassigned;

        public OutputSlotManager(
            Action<IReadOnlyCollection<string>> virtualSonyRegisteredCallback = null)
        {
            this.virtualSonyRegisteredCallback = virtualSonyRegisteredCallback;
            outputSlots = new OutSlotDevice[ControlService.CURRENT_DS4_CONTROLLER_LIMIT];
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                outputSlots[i] = new OutSlotDevice(i);
            }

            lastSlotIndex = outputSlots.Length > 0 ? outputSlots.Length - 1 : 0;

            queueLocker = new ReaderWriterLockSlim();
        }

        public void ShutDown()
        {
        }

        public void Stop(bool immediate = false)
        {
            UnplugRemainingControllers(immediate);
            Stopwatch queueWait = Stopwatch.StartNew();
            while (RunningQueue && queueWait.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(1);
            }

            if (RunningQueue)
            {
                ControlService.StartupDiag("OutputSlotManager.Stop timed out waiting for queued output task");
            }

            deviceDict.Clear();
            revDeviceDict.Clear();
        }

        public OutputDevice AllocateController(OutContType contType)
        {
            contType = contType.Normalize();
            OutputDevice outputDevice = null;
            switch (contType)
            {
                case OutContType.ViiperX360:
                    outputDevice = new ViiperOutDevice(contType, ViiperVirtualDeviceType.Xbox360);
                    break;
                case OutContType.ViiperDS4:
                    outputDevice = new ViiperOutDevice(contType,
                        ViiperVirtualDeviceType.DualShock4);
                    break;
                case OutContType.ViiperDualSense:
                    outputDevice = new ViiperOutDevice(contType,
                        ViiperVirtualDeviceType.DualSense);
                    break;
                case OutContType.ViiperDualSenseEdge:
                    outputDevice = new ViiperOutDevice(contType,
                        ViiperVirtualDeviceType.DualSenseEdge);
                    break;
                case OutContType.ViiperSwitch2Pro:
                    outputDevice = new ViiperOutDevice(contType, ViiperVirtualDeviceType.Switch2Pro);
                    break;
                case OutContType.None:
                default:
                    break;
            }

            return outputDevice;
        }

        private int FindEmptySlot()
        {
            int result = -1;
            for (int i = 0; i < outputDevices.Length && result == -1; i++)
            {
                OutputDevice tempdev = outputDevices[i];
                if (tempdev == null)
                {
                    result = i;
                }
            }

            return result;
        }

        public void DeferredPlugin(OutputDevice outputDevice, int inIdx, string inDisplayString,
            OutputDevice[] outdevs, OutContType contType)
        {
            contType = contType.Normalize();
            ControlService.StartupDiag($"OutputSlotManager.DeferredPlugin enter inIdx={inIdx} contType={contType} outputNull={outputDevice == null}");
            // releases ReaderWriterLockSlim when locker goes out of scope
            using WriteLocker locker = new WriteLocker(queueLocker);
            //queuedTasks++;
            //Action tempAction = new Action(() =>
            {
                int slot = FindEmptySlot();
                ControlService.StartupDiag($"OutputSlotManager.DeferredPlugin emptySlot={slot + 1} inIdx={inIdx} contType={contType}");
                if (slot != -1)
                {
                    // Record every VIIPER Sony output so its complete USB/IP
                    // HID cannot be re-ingested as a physical input.
                    HashSet<string> beforeVirtualSony = null;
                    if (contType == OutContType.ViiperDS4 ||
                        contType == OutContType.ViiperDualSense ||
                        contType == OutContType.ViiperDualSenseEdge)
                    {
                        beforeVirtualSony = DS4Devices.
                            SnapshotBeforeOwnVirtualSony();
                        DS4Devices.BeginOwnVirtualSonyConnect();
                    }

                    try
                    {
                        ControlService.StartupDiag($"OutputSlotManager.Connect begin slot={slot + 1} type={contType} output={outputDevice.GetType().Name}");
                        outputDevice.Connect();
                        ControlService.StartupDiag($"OutputSlotManager.Connect end slot={slot + 1} type={contType}");
                    }
                    catch (Win32Exception e)
                    {
                        ControlService.StartupDiag($"OutputSlotManager.Connect Win32Exception slot={slot + 1} type={contType} error={e.ErrorCode} message={e.Message}");
                        // Leave task immediately if connect call failed
                        //queuedTasks--;
                        AppLogger.LogToGui($"Failed to plug in virtual {contType.ToDisplayName()} controller: {e.Message}", true);

                        if (beforeVirtualSony != null)
                        {
                            DS4Devices.EndOwnVirtualSonyConnect();
                        }

                        return;
                    }
                    catch (Exception e)
                    {
                        AppLogger.LogToGui($"Failed to plug in virtual {contType.ToDisplayName()} controller: {e.Message}", true);
                        if (beforeVirtualSony != null)
                        {
                            DS4Devices.EndOwnVirtualSonyConnect();
                        }

                        return;
                    }

                    if (beforeVirtualSony != null)
                    {
                        if (outputDevice is ViiperOutDevice viiperOutput &&
                            viiperOutput.BackendMode ==
                                ViiperBackendMode.NativeUde)
                        {
                            // Native children are rejected by their exact PnP
                            // ancestry. Do not run the USB/IP-era HID delta
                            // wait/registration loop.
                            DS4Devices.EndOwnVirtualSonyConnect();
                        }
                        else
                        {
                            DS4Devices.RegisterOwnVirtualSonyAsync(
                                beforeVirtualSony,
                                virtualSonyRegisteredCallback);
                            // The asynchronous registration worker now owns
                            // the matching EndOwnVirtualSonyConnect call.
                        }
                        beforeVirtualSony = null;
                    }

                    AppLogger.LogToGui($"Plugging in virtual {contType.ToDisplayName()} Controller in output slot #{slot + 1}", false);

                    outputDevices[slot] = outputDevice;
                    deviceDict.Add(slot, outputDevice);
                    revDeviceDict.Add(outputDevice, slot);
                    outputSlots[slot].AttachedDevice(outputDevice, contType, inIdx, inDisplayString);
                    if (inIdx != -1)
                    {
                        outdevs[inIdx] = outputDevice;
                        outputSlots[slot].CurrentInputBound = OutSlotDevice.InputBound.Bound;
                    }
                    SlotAssigned?.Invoke(this, slot, outputSlots[slot]);
                    ControlService.StartupDiag($"OutputSlotManager.DeferredPlugin assigned slot={slot + 1} inIdx={inIdx} type={contType}");
                }
                else
                {
                    ControlService.StartupDiag($"OutputSlotManager.DeferredPlugin no empty slot inIdx={inIdx} type={contType}");
                }
            };

            //queuedTasks--;
        }

        public void DeferredRemoval(OutputDevice outputDevice, int inIdx,
            OutputDevice[] outdevs, bool immediate = false)
        {
            _ = immediate;
            ControlService.StartupDiag($"OutputSlotManager.DeferredRemoval enter inIdx={inIdx} outputNull={outputDevice == null}");

            // releases ReaderWriterLockSlim when locker goes out of scope
            using WriteLocker locker = new WriteLocker(queueLocker);
            //queuedTasks++;

            {
                if (revDeviceDict.TryGetValue(outputDevice, out int slot))
                {
                    ControlService.StartupDiag($"OutputSlotManager.DeferredRemoval found slot={slot + 1} type={outputDevice.GetDeviceType()}");
                    //int slot = revDeviceDict[outputDevice];
                    outputDevices[slot] = null;
                    deviceDict.Remove(slot);
                    revDeviceDict.Remove(outputDevice);

                    ControlService.StartupDiag($"OutputSlotManager.RemoveFeedbacks begin slot={slot + 1}");
                    outputDevice.RemoveFeedbacks();
                    ControlService.StartupDiag($"OutputSlotManager.RemoveFeedbacks end slot={slot + 1}");
                    ControlService.StartupDiag($"OutputSlotManager.Disconnect begin slot={slot + 1}");
                    outputDevice.Disconnect();
                    ControlService.StartupDiag($"OutputSlotManager.Disconnect end slot={slot + 1}");

                    if (inIdx != -1)
                    {
                        outdevs[inIdx] = null;
                    }

                    OutContType removedType = outputSlots[slot].CurrentType;
                    outputSlots[slot].DetachDevice();
                    SlotUnassigned?.Invoke(this, slot, outputSlots[slot]);
                    AppLogger.LogToGui($"Unplugging virtual {removedType.ToDisplayName()} Controller from output slot #{slot + 1}",false);
                    ControlService.StartupDiag($"OutputSlotManager.DeferredRemoval unassigned slot={slot + 1}");

                    //if (!immediate)
                    //{
                        //    Task.Delay(DELAY_TIME).Wait();
                    //}
                }
                else
                {
                    ControlService.StartupDiag("OutputSlotManager.DeferredRemoval output not found in reverse map");
                }
            };

            //queuedTasks--;
        }

        public OutSlotDevice FindOpenSlot()
        {
            OutSlotDevice temp = null;
            for (int i = 0; i < outputSlots.Length; i++)
            {
                OutSlotDevice tmp = outputSlots[i];
                if (tmp.CurrentInputBound == OutSlotDevice.InputBound.Unbound &&
                    tmp.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.UnAttached)
                {
                    temp = tmp;
                    break;
                }
            }

            return temp;
        }

        public bool SlotAvailable(int slotNum)
        {
            bool result;
            if (slotNum < 0 && slotNum > lastSlotIndex)
            {
                throw new ArgumentOutOfRangeException("Invalid slot number");
            }

            //slotNum -= 1;
            result = outputSlots[slotNum].CurrentAttachedStatus == OutSlotDevice.AttachedStatus.UnAttached;
            return result;
        }

        public OutSlotDevice GetOutSlotDevice(int slotNum)
        {
            OutSlotDevice temp;
            if (slotNum < 0 && slotNum > lastSlotIndex)
            {
                throw new ArgumentOutOfRangeException("Invalid slot number");
            }

            //slotNum -= 1;
            temp = outputSlots[slotNum];
            return temp;
        }

        public OutSlotDevice GetOutSlotDevice(OutputDevice outputDevice)
        {
            OutSlotDevice temp = null;
            if (outputDevice != null &&
                revDeviceDict.TryGetValue(outputDevice, out int slotNum))
            {
                temp = outputSlots[slotNum];
            }

            return temp;
        }

        public OutSlotDevice FindExistUnboundSlotType(OutContType contType)
        {
            OutSlotDevice temp = null;
            string devtype = contType.ToString();
            for (int i = 0; i < outputSlots.Length; i++)
            {
                OutSlotDevice tmp = outputSlots[i];
                if (tmp.CurrentInputBound == OutSlotDevice.InputBound.Unbound &&
                    (tmp.CurrentAttachedStatus == OutSlotDevice.AttachedStatus.Attached &&
                    (tmp.OutputDevice != null && tmp.OutputDevice.GetDeviceType() == devtype)))
                {
                    temp = tmp;
                    break;
                }
            }

            return temp;
        }

        public void UnplugRemainingControllers(bool immediate=false)
        {
            _ = immediate;

            // releases ReaderWriterLockSlim when locker goes out of scope
            using WriteLocker locker = new WriteLocker(queueLocker);
            //queuedTasks++;
            {
                int slotIdx = 0;
                foreach (OutSlotDevice device in outputSlots)
                {
                    if (device.OutputDevice != null)
                    {
                        outputDevices[slotIdx] = null;
                        device.OutputDevice.Disconnect();

                        device.DetachDevice();
                        SlotUnassigned?.Invoke(this, slotIdx, outputSlots[slotIdx]);
                        //if (!immediate)
                        //{
                        //    Task.Delay(DELAY_TIME).Wait();
                        //}
                    }

                    slotIdx++;
                }
            };

            //queuedTasks--;
        }
    }
}
