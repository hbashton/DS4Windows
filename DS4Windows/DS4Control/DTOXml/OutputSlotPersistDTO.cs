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
using System.Xml.Serialization;
using DS4Windows;

namespace DS4WinWPF.DS4Control.DTOXml
{
    [XmlRoot("OutputSlots")]
    public class OutputSlotPersistDTO : IDTO<OutputSlotManager>
    {
        [XmlAttribute("app_version")]
        public string AppVersion
        {
            get => Global.exeversion;
            set { }
        }

        [XmlElement("Slot")] // Use XmlElement here to skip container element
        public List<OutputSlotSerializer> SlotItems
        {
            get; set;
        }

        public OutputSlotPersistDTO()
        {
            SlotItems = new List<OutputSlotSerializer>();
        }

        public void MapFrom(OutputSlotManager source)
        {
            foreach (OutSlotDevice dev in source.OutputSlots)
            {
                if (dev.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Permanent)
                {
                    OutputSlotSerializer tempSlot = new OutputSlotSerializer()
                    {
                        Index = dev.Index,
                        DeviceType = dev.PermanentType.Normalize(),
                    };

                    SlotItems.Add(tempSlot);
                }
            }
        }

        public void MapTo(OutputSlotManager destination)
        {
            foreach(OutputSlotSerializer tempSlot in SlotItems)
            {
                OutSlotDevice tempDev = null;
                if (tempSlot.Index >= 0 && tempSlot.Index <= 3)
                {
                    int idx = tempSlot.Index;
                    tempDev = destination.OutputSlots[idx];
                }

                if (tempDev != null)
                {
                    if (tempSlot.DeviceType == OutContType.None)
                    {
                        continue;
                    }

                    tempDev.CurrentReserveStatus = OutSlotDevice.ReserveStatus.Permanent;
                    tempDev.PermanentType = tempSlot.DeviceType.Normalize();
                }
            }
        }

        internal static OutContType ParseOutputDeviceType(string value, OutContType fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            switch (value.Trim())
            {
                case "Xbox 360":
                case "Xbox360":
                case "X360":
                    return OutContType.ViiperX360;
                case "DualShock 4":
                case "DualShock4":
                case "DS4":
                    return OutContType.ViiperDS4;
                case "Xbox 360 (VIIPER)":
                case "ViiperXbox360":
                case "ViiperX360":
                    return OutContType.ViiperX360;
                case "DualShock 4 (VIIPER)":
                case "ViiperDualShock4":
                case "ViiperDS4":
                    return OutContType.ViiperDS4;
                case "DualSense (VIIPER)":
                case "DualSense":
                case "ViiperDualSense":
                    return OutContType.ViiperDualSense;
                case "DualSense Edge (VIIPER)":
                case "DualSenseEdge":
                case "ViiperDualSenseEdge":
                    return OutContType.ViiperDualSenseEdge;
                case "Switch 2 Pro (VIIPER)":
                case "Switch2Pro":
                case "ViiperSwitch2Pro":
                    return OutContType.ViiperSwitch2Pro;
                case "Xbox One (VIIPER)":
                case "Xbox One / Series":
                case "XboxOne":
                case "ViiperXboxOne":
                    return OutContType.ViiperXboxOne;
                case "None":
                    return OutContType.None;
            }

            if (Enum.TryParse(value, true, out OutContType parsed) &&
                Enum.IsDefined(typeof(OutContType), parsed))
            {
                return parsed.Normalize();
            }

            return fallback;
        }

        internal static string FormatOutputDeviceType(OutContType value)
        {
            return value.Normalize() switch
            {
                OutContType.None => "None",
                OutContType.ViiperX360 => "ViiperX360",
                OutContType.ViiperDS4 => "ViiperDS4",
                OutContType.ViiperDualSense => "ViiperDualSense",
                OutContType.ViiperDualSenseEdge => "ViiperDualSenseEdge",
                OutContType.ViiperSwitch2Pro => "ViiperSwitch2Pro",
                OutContType.ViiperXboxOne => "ViiperXboxOne",
                _ => "ViiperX360",
            };
        }
    }

    public class OutputSlotSerializer
    {
        [XmlAttribute("idx")]
        public int Index
        {
            get; set;
        } = 0;

        [XmlIgnore]
        public OutContType DeviceType
        {
            get; set;
        }

        [XmlElement("DeviceType")]
        public string DeviceTypeString
        {
            get => OutputSlotPersistDTO.FormatOutputDeviceType(DeviceType);
            set => DeviceType = OutputSlotPersistDTO.ParseOutputDeviceType(value, OutContType.None);
        }
    }
}
